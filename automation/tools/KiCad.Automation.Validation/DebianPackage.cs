using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record DebianPackageResult(string Package, string PackageName, string Version,
    string Commit, string SourceSha256, string Depends, long Bytes, string Sha256,
    IReadOnlyList<string> OmittedOptionalFiles,
    bool CleanHostVerified = false, bool QualifyingDelivery = false);

public static partial class DebianPackage
{
    // Dynamic runtime dependencies are not all visible in DT_NEEDED.
    // .NET: https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian#dependencies
    // Poppler is the approved source-document backend; ngspice is KiCad's engine.
    public static readonly string[] RuntimeDependencies =
        ["ca-certificates", "libc6", "libgcc-s1", "libgssapi-krb5-2", "libicu76",
         "libssl3t64", "libstdc++6", "tzdata", "poppler-utils", "libngspice0"];

    public static string CombineDependencies(string shlibsOutput)
    {
        const string prefix = "shlibs:Depends=";
        string[] lines = shlibsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1 || !lines[0].StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Debian dependency analysis did not return one dependency list.");
        var dependencies = lines[0][prefix.Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (dependencies.Count == 0 || dependencies.Any(d => d.Split('|').Any(a => !DependencyPattern().IsMatch(a.Trim()))))
            throw new InvalidDataException("Debian dependency analysis returned an invalid dependency.");
        foreach (string required in RuntimeDependencies)
            if (!dependencies.Any(d => d == required || d.StartsWith(required + " ", StringComparison.Ordinal)))
                dependencies.Add(required);
        return string.Join(", ", dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    public static async Task<DebianPackageResult> CreateAsync(string cataloguePath, string output, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Debian package preparation requires Linux x64.");
        if (!Path.IsPathFullyQualified(cataloguePath) || !Path.IsPathFullyQualified(output))
            throw new ArgumentException("Provide absolute catalogue and output paths.");
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Output must be a new directory.");
        var manifest = JsonSerializer.Deserialize<DownloadManifest>(await File.ReadAllTextAsync(cataloguePath, token),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("A catalogue is required.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported catalogue version.");
        var artifact = manifest.Artifacts.Single(a => a.Platform == "linux-x64");
        Evidence.RequireCommit(artifact.Commit);
        if (!HashPattern().IsMatch(artifact.Sha256) || !HashPattern().IsMatch(artifact.SourceSha256)
            || !VersionPattern().IsMatch(artifact.Version))
            throw new InvalidDataException("Invalid package identity.");
        string archive = LinuxPackage.ContainedPath(Path.GetDirectoryName(cataloguePath)!, artifact.FileName);
        if (new FileInfo(archive).Length != artifact.Bytes || Evidence.Hash(archive) != artifact.Sha256)
            throw new InvalidDataException("Source application archive does not match its catalogue.");

        Directory.CreateDirectory(output);
        string work = Directory.CreateDirectory(Path.Combine(output, "work")).FullName;
        string debian = Directory.CreateDirectory(Path.Combine(work, "debian")).FullName;
        // Unique package names and prefixes allow old running versions to remain
        // installed. Do not overwrite another KiCad installation or launch alias.
        string packageName = "kicad-codex-" + artifact.Sha256[..12];
        string packageRoot = Directory.CreateDirectory(Path.Combine(debian, packageName)).FullName;
        string version = "0~" + artifact.Version;
        string bundle = Directory.CreateDirectory(Path.Combine(packageRoot, "opt", "kicad-codex", artifact.Version)).FullName;
        await using (var compressed = File.OpenRead(archive))
        await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            await TarFile.ExtractToDirectoryAsync(gzip, bundle, overwriteFiles: false, token);
        using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "package.json"), token)))
            if (metadata.RootElement.GetProperty("commit").GetString() != artifact.Commit
                || metadata.RootElement.GetProperty("sourceSha256").GetString() != artifact.SourceSha256)
                throw new InvalidDataException("Extracted package source identity does not match.");
        string runtime = Path.Combine(bundle, "runtime");
        LinuxStaging.VerifyTree(runtime);
        // Optional LTTng 2.12 tracing is incompatible with Debian 13's ABI.
        // Only remove this optional provider from this new package copy, not
        // from the frozen archive/runtime. Never suppress other missing libs.
        // https://learn.microsoft.com/en-us/dotnet/core/diagnostics/trace-perfcollect-lttng
        const string traceProvider = "runtime/lib/kicad-automation/libcoreclrtraceptprovider.so";
        var omitted = new List<string>();
        string optionalTrace = Path.Combine(bundle, traceProvider);
        if (File.Exists(optionalTrace))
        {
            File.Delete(optionalTrace);
            omitted.Add(traceProvider);
        }
        string control = "Package: " + packageName + "\nVersion: " + version + "\nArchitecture: amd64\n"
            + "Maintainer: holyglory <6716019+holyglory@users.noreply.github.com>\n"
            + "Section: electronics\nPriority: optional\n"
            + "Description: Preliminary Codex-operated KiCad and self-contained MCP\n"
            + " Versioned preview installation; not cross-platform or update qualification.\n";
        await File.WriteAllTextAsync(Path.Combine(debian, "control"), "Source: " + packageName
            + "\nSection: electronics\nPriority: optional\nMaintainer: holyglory <6716019+holyglory@users.noreply.github.com>\n\n"
            + control, token);
        string metadataDir = Directory.CreateDirectory(Path.Combine(packageRoot, "DEBIAN")).FullName;
        await File.WriteAllTextAsync(Path.Combine(metadataDir, "control"), control, token);
        var elfFiles = new List<string>();
        foreach (string path in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(path).LinkTarget is not null) continue;
            using var file = File.OpenRead(path);
            byte[] magic = new byte[4];
            if (file.Read(magic) == 4 && magic.AsSpan().SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }))
                elfFiles.Add(path);
        }
        if (elfFiles.Count == 0) throw new InvalidDataException("The package contains no native executables.");
        var shlibs = new List<string>();
        string privateLib = Path.Combine(runtime, "lib");
        foreach (string path in elfFiles.Where(p => Path.GetDirectoryName(p) == privateLib))
        {
            string dynamic = await LinuxPackage.RunAsync("readelf", ["-d", path], work, token);
            Match soname = SonamePattern().Match(dynamic);
            if (soname.Success)
                shlibs.Add(soname.Groups[1].Value + " " + soname.Groups[2].Value + " " + packageName);
        }
        await File.WriteAllTextAsync(Path.Combine(debian, "shlibs.local"),
            string.Join("\n", shlibs.Distinct(StringComparer.Ordinal)) + "\n", token);
        var arguments = new List<string> { "-O", "-l" + privateLib, "-S" + packageRoot, "-x" + packageName };
        arguments.AddRange(elfFiles.Select(p => "-e" + p));
        string analyzed = await LinuxPackage.RunAsync("dpkg-shlibdeps", arguments.ToArray(), work, token);
        string depends = CombineDependencies(analyzed);
        long installedKiB = (Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
            .Sum(p => new FileInfo(p).Length) + 1023) / 1024;
        await File.WriteAllTextAsync(Path.Combine(metadataDir, "control"),
            control + "Installed-Size: " + installedKiB + "\nDepends: " + depends + "\n", token);
        NormalizePermissions(packageRoot);
        string package = Path.Combine(output, packageName + "_" + version + "_amd64.deb");
        await LinuxPackage.RunAsync("dpkg-deb", ["--root-owner-group", "--build", packageRoot, package], work, token);
        var result = new DebianPackageResult(package, packageName, version, artifact.Commit,
            artifact.SourceSha256, depends, new FileInfo(package).Length, Evidence.Hash(package), omitted);
        await File.WriteAllTextAsync(Path.Combine(output, "debian-package.json"),
            JsonSerializer.Serialize(result, Evidence.JsonOptions), token);
        return result;
    }

    public static void NormalizePermissions(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Debian permissions require Unix.");
        const UnixFileMode readable = UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            if (new DirectoryInfo(directory).LinkTarget is null)
                File.SetUnixFileMode(directory, readable | executable);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is not null) continue;
            UnixFileMode mode = File.GetUnixFileMode(file);
            File.SetUnixFileMode(file, readable | ((mode & executable) != 0 ? executable : 0));
        }
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9+.-]*(?::[a-z0-9-]+)?(?: \((?:>=|<=|=|>>|<<) [A-Za-z0-9.+:~_-]+\))?\z")]
    private static partial Regex DependencyPattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.-]{0,79}\z")]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\(SONAME\).*?\[(lib[^\]\s]+)\.so\.([^\]\s]+)\]")]
    private static partial Regex SonamePattern();
}
