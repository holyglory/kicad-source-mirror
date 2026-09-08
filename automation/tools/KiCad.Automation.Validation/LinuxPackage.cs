using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record LinuxPackageRequest(string StagingReceipt, string Repository,
    string Commit, string Version, string Output);

public static class LinuxPackage
{
    public static async Task<DownloadManifest> CreateAsync(LinuxPackageRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux packaging must run on Linux.");
        Evidence.RequireCommit(request.Commit);
        if (string.IsNullOrEmpty(request.Version) || request.Version.Length > 80
            || request.Version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'))
            throw new ArgumentException("Use an ASCII letter/digit, hyphen and period package version.");
        foreach (string path in new[] { request.StagingReceipt, request.Repository, request.Output })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Package paths must be absolute.");
        if (Directory.Exists(request.Output) || File.Exists(request.Output))
            throw new ArgumentException("The package output directory must not already exist.");
        var stage = JsonSerializer.Deserialize<LinuxStageResult>(await File.ReadAllTextAsync(request.StagingReceipt, token))
            ?? throw new InvalidDataException("A staging receipt is required.");
        if (stage.SchemaVersion != 1 || stage.Status != "staged" || stage.Files is null || stage.Files.Count == 0)
            throw new InvalidDataException("The staging receipt does not identify a completed install.");
        string stagedRoot = Path.Combine(stage.Directory, "root");
        string prefix = Path.Combine(stagedRoot, "usr/local");
        LinuxStaging.VerifyTree(prefix);
        foreach (var file in stage.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = ContainedPath(stagedRoot, file.Path);
            if (new FileInfo(path).Length != file.Bytes || Evidence.Hash(path) != file.Sha256)
                throw new InvalidDataException("Staged bytes no longer match their receipt: " + file.Path);
        }
        await RequireSourceAsync(request.Repository, request.Commit, token);
        var environment = new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = Path.Combine(prefix, "lib"),
            ["KICAD_STOCK_DATA_HOME"] = Path.Combine(prefix, "share/kicad")
        };
        string scratch = Directory.CreateTempSubdirectory("kicad-package-version-").FullName;
        string nativeCommit;
        try
        {
            environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config");
            environment["XDG_CACHE_HOME"] = Path.Combine(scratch, "cache");
            nativeCommit = (await RunAsync(Path.Combine(prefix, "bin/kicad-cli"),
                ["version", "--format", "commit"], request.Repository, token, environment)).Trim();
        }
        finally { Directory.Delete(scratch, true); }
        if (nativeCommit != request.Commit)
            throw new InvalidDataException("The installed native executable was not built from the requested commit.");

        Directory.CreateDirectory(request.Output);
        string work = Directory.CreateDirectory(Path.Combine(request.Output, "assembly")).FullName;
        string runtime = Directory.CreateDirectory(Path.Combine(work, "runtime")).FullName;
        CopyInstall(prefix, runtime, prefix, token);
        string notices = Directory.CreateDirectory(Path.Combine(work, "licenses")).FullName;
        foreach (string license in Directory.EnumerateFiles(request.Repository, "LICENSE*"))
            File.Copy(license, Path.Combine(notices, Path.GetFileName(license)));
        File.Copy(Path.Combine(request.Repository, "AUTHORS.txt"), Path.Combine(notices, "AUTHORS.txt"));
        File.Copy("/usr/share/doc/libnng1/copyright", Path.Combine(notices, "NNG-Debian-copyright"));
        string sourceName = "kicad-codex-" + request.Version + "-source.tar.gz";
        string source = Path.Combine(request.Output, sourceName);
        await RunAsync("git", ["archive", "--format=tar.gz", "--output", source, request.Commit],
            request.Repository, token);
        string sourceHash = Evidence.Hash(source);
        await File.WriteAllTextAsync(Path.Combine(work, "package.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, version = request.Version, commit = request.Commit,
            sourceSha256 = sourceHash, platform = "linux-x64", testedDistribution = "Debian 13",
            qualifyingDelivery = false, automaticUpdating = false
        }, Evidence.JsonOptions), token);
        await File.WriteAllTextAsync(Path.Combine(work, "README.txt"),
            "Preliminary Codex-operated KiCad build for Debian 13 x86_64.\n" +
            "Run ./kicad-codex to open KiCad, or ./kicad-mcp for the STDIO tool service.\n" +
            "The .NET runtime and NNG are included. Native system-library dependencies still need qualification and installation.\n" +
            "Automatic updating, native Mac builds and the complete engineering workflow are not qualified.\n" +
            "This archive does not establish a qualifying delivery or cross-platform readiness.\n" +
            "Corresponding source: " + sourceName + "\n", token);
        await WriteLauncherAsync(work, "kicad-codex", "runtime/bin/kicad", token);
        await WriteLauncherAsync(work, "kicad-mcp", "runtime/lib/kicad-automation/kicad-mcp", token);
        await RequireSourceAsync(request.Repository, request.Commit, token);
        string packageName = "kicad-codex-" + request.Version + "-debian13-x64.tar.gz";
        string package = Path.Combine(request.Output, packageName);
        await using (var file = new FileStream(package, FileMode.CreateNew, FileAccess.Write))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await TarFile.CreateFromDirectoryAsync(work, gzip, includeBaseDirectory: false, token);
        token.ThrowIfCancellationRequested();
        var manifest = new DownloadManifest(1,
        [
            new(packageName, "linux-x64", request.Version, request.Commit, sourceHash,
                new FileInfo(package).Length, Evidence.Hash(package)),
            new(sourceName, "source", request.Version, request.Commit, sourceHash,
                new FileInfo(source).Length, sourceHash)
        ]);
        // Only complete archives become discoverable. A failed/cancelled build
        // leaves its private assembly/evidence intact and has no public catalogue.
        string pending = Path.Combine(request.Output, ".downloads.pending.json");
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
        File.Move(pending, Path.Combine(request.Output, "downloads.json"));
        return manifest;

    }

    internal static async Task WriteLauncherAsync(string work, string name, string executable, CancellationToken token)
    {
        string path = Path.Combine(work, name);
        string updateContext = name == "kicad-codex" ?
            "kicad_version_dir=$(CDPATH= cd -- \"$kicad_bundle_dir/..\" && pwd -P)\n" +
            "kicad_versions_dir=$(dirname -- \"$kicad_version_dir\")\n" +
            "if [ -z \"${KICAD_AUTOMATION_UPDATE_HELPER:-}\" ] && [ -z \"${KICAD_AUTOMATION_UPDATE_CONFIG:-}\" ] && " +
            "[ \"$(basename -- \"$kicad_versions_dir\")\" = versions ] && " +
            "[ -f \"$kicad_versions_dir/../publisher.json\" ] && [ -f \"$kicad_version_dir/version.json\" ] && " +
            "[ -f \"$kicad_version_dir/installed-envelope.json\" ] && [ -f \"$kicad_version_dir/update-config.json\" ]; then\n" +
            "  export KICAD_AUTOMATION_UPDATE_HELPER=\"$kicad_bundle_dir/runtime/lib/kicad-automation/kicad-mcp\"\n" +
            "  export KICAD_AUTOMATION_UPDATE_CONFIG=\"$kicad_version_dir/update-config.json\"\n" +
            "fi\n" : "";
        await File.WriteAllTextAsync(path,
            "#!/bin/sh\nset -eu\n" +
            // Resolve the physical version, never retain a mutable current link
            // in paths a live process can later use for resources or libraries.
            "kicad_bundle_dir=$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd -P)\n" +
            "unset APPDIR KICAD_RUN_FROM_BUILD_DIR\n" +
            "export LD_LIBRARY_PATH=\"$kicad_bundle_dir/runtime/lib\"\n" +
            "export KICAD_STOCK_DATA_HOME=\"$kicad_bundle_dir/runtime/share/kicad\"\n" +
            // Only the verified bootstrap layout supplies automatic context.
            // Explicit operator overrides and the general MCP launcher remain
            // independent; never scan a project or download folder for config.
            updateContext +
            "exec \"$kicad_bundle_dir/" + executable + "\" \"$@\"\n", token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public static string ContainedPath(string root, string relative)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (Path.IsPathFullyQualified(relative)) throw new InvalidDataException("Expected a package-relative path.");
        string path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Package path escapes its root.");
        return path;
    }

    public static async Task RequireSourceAsync(string repository, string commit, CancellationToken token)
    {
        Evidence.RequireCommit(commit);
        if ((await RunAsync("git", ["rev-parse", "HEAD"], repository, token)).Trim() != commit)
            throw new InvalidDataException("Source checkout does not match the exact package commit.");
        await RunAsync("git", ["diff", "--quiet", "HEAD", "--"], repository, token);
        string tracked = await RunAsync("git", ["ls-tree", "-r", "--name-only", "-z", commit], repository, token);
        if (tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(IsPrivateContext))
            throw new InvalidDataException("The public source commit includes private workspace records.");
        string untracked = await RunAsync("git", ["ls-files", "--others", "--exclude-standard", "-z"], repository, token);
        foreach (string path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Private management/context files are not build inputs or public
            // source. Unknown uncommitted material requires explicit review.
            if (IsPrivateContext(path)) continue;
            throw new InvalidDataException("Uncommitted source cannot be represented by the package commit: " + path);
        }
    }

    private static bool IsPrivateContext(string path) =>
        path.StartsWith(".serena/", StringComparison.Ordinal)
        || path.StartsWith("automation/.serena/", StringComparison.Ordinal)
        || path.StartsWith("UserIssueLedgers/", StringComparison.Ordinal)
        || path.StartsWith("automation/reports/", StringComparison.Ordinal)
        || path == "security-assumptions.md";

    private static void CopyInstall(string source, string destination, string sourceRoot, CancellationToken token)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (Path.IsPathFullyQualified(link))
                    throw new InvalidDataException("Absolute install links are not relocatable.");
                _ = ContainedPath(sourceRoot, Path.GetRelativePath(sourceRoot, Path.Combine(source, link)));
                File.CreateSymbolicLink(target, link);
            }
            else if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(target);
                CopyInstall(entry.FullName, target, sourceRoot, token);
            }
            else
            {
                File.Copy(entry.FullName, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    internal static async Task<string> RunAsync(string executable, string[] args, string directory,
        CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Remove("APPDIR");
        start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using Process process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        finally { await Task.WhenAll(stdout, stderr); }
        if (process.ExitCode != 0)
            throw new InvalidDataException(Path.GetFileName(executable) + " " + string.Join(" ", args.Take(2))
                + " exited " + process.ExitCode + ": " + (await stderr).Trim());
        return await stdout;
    }
}
