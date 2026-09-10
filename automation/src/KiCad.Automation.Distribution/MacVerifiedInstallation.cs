using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Distribution;

public sealed record InstalledMacUpdate(string Root, string VersionDirectory, string ManagerDirectory,
    string UpdateConfiguration, string ManifestSha256, string Commit, string Platform)
{
    public string ApplicationBundle => Path.Combine(VersionDirectory, "install/KiCad.app");
    public string NativeExecutable => Path.Combine(ApplicationBundle, "Contents/MacOS/kicad");
    public string McpLauncher => Path.Combine(VersionDirectory, "kicad-mcp");
}
public sealed record RegisteredMacUpdate(InstalledMacUpdate Version, string ExpectedTarget, bool Reused);
internal sealed record MacInstallationPolicy(int SchemaVersion, string Origin, string PublisherKeySpki, string Channel, string Platform);
internal sealed record MacVersionRegistration(int SchemaVersion, string ManifestSha256, string PublisherKeySha256, string PayloadSha256);

/// <summary>Verified per-operator Mac versions. Registration leaves selection and editor processes alone.</summary>
public static partial class MacVerifiedInstallation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    public static async Task<InstalledMacUpdate> InstallAsync(string installationRoot, string archivePath,
        ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> trustedPublisherSpki, Uri origin, string channel,
        CancellationToken token = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac installation requires native macOS.");
        string platform = Platform();
        string root = RootPath(installationRoot, exists: false);
        if (!Path.IsPathFullyQualified(archivePath)) throw new ArgumentException("Use an absolute Mac archive path.");
        string parent = Path.GetDirectoryName(root)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The installation parent does not exist.");
        RequireAbsent(root);
        byte[] bytes = envelope.ToArray(), key = trustedPublisherSpki.ToArray();
        var manifest = UpdateManifestCodec.Verify(bytes, key, channel);
        _ = manifest.ForInstallation(platform, "tar.gz") ?? throw new InvalidDataException("No archive matches this Mac architecture.");
        using var location = new UpdateDownloader(origin);
        var policy = new MacInstallationPolicy(1, origin.AbsoluteUri, Convert.ToBase64String(key), channel, platform);
        string work = Path.Combine(parent, ".kicad-mac-install-" + Guid.NewGuid().ToString("N"));
        RequireAbsent(work);
        Directory.CreateDirectory(work);
        bool published = false;
        try
        {
            await WriteNew(Path.Combine(work, "publisher.json"), policy, token);
            Directory.CreateDirectory(Path.Combine(work, "versions"));
            Directory.CreateDirectory(Path.Combine(work, "state"));
            Directory.CreateDirectory(Path.Combine(work, "staging"));
            string version = Path.Combine(work, "versions", manifest.PayloadSha256);
            var result = await PrepareVersion(version, root, archivePath, bytes, manifest, policy, token);
            await PosixUpdateActivation.InitializeAsync(Path.Combine(work, "manager"), Path.Combine(version, "payload"), token);
            Directory.CreateSymbolicLink(Path.Combine(work, "KiCad.app"), "manager/current/install/KiCad.app");
            string launcher = Path.Combine(work, "kicad-mcp");
            await File.WriteAllTextAsync(launcher, "#!/bin/sh\nset -eu\nkicad_install_root=$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd -P)\nexec \"$kicad_install_root/manager/current/kicad-mcp\" \"$@\"\n", token);
            File.SetUnixFileMode(launcher, (UnixFileMode)0x1ed);
            await WriteNew(Path.Combine(work, "installed.json"), new
                { schemaVersion = 1, status = "installed", result, automaticUpdatingQualified = false, nativeEditorRestarted = false }, token);
            token.ThrowIfCancellationRequested();
            RequireAbsent(root);
            Directory.Move(work, root);
            published = true;
            return result;
        }
        finally { if (!published) MacUpdateStager.RemoveOwnedTree(work); }
    }

    public static async Task<RegisteredMacUpdate> RegisterAsync(string installationRoot, string expectedTarget,
        string archivePath, ReadOnlyMemory<byte> envelope, CancellationToken token = default)
    {
        string root = RootPath(installationRoot);
        var policy = await Policy(root, token);
        if (!Path.IsPathFullyQualified(archivePath)) throw new ArgumentException("Use an absolute candidate archive path.");
        byte[] key = Convert.FromBase64String(policy.PublisherKeySpki), bytes = envelope.ToArray();
        using var ownership = Lock(Path.Combine(root, "registration.lock"));
        string manager = Path.Combine(root, "manager");
        if (PosixUpdateActivation.InspectTarget(manager) != expectedTarget)
            throw new InvalidDataException("The Mac installation selection changed before registration.");
        var baseline = await ReadVersion(CurrentVersion(root, expectedTarget), policy, token);
        var candidate = UpdateManifestCodec.Verify(bytes, key, policy.Channel, new(baseline.Release.Sequence, baseline.PayloadSha256));
        await RequireAccepted(root, policy, baseline, candidate, token);
        string destination = Path.Combine(root, "versions", candidate.PayloadSha256);
        if (Directory.Exists(destination))
        {
            var existing = await ReadVersion(destination, policy, token);
            if (existing.PayloadSha256 != candidate.PayloadSha256) throw new InvalidDataException("Existing Mac candidate differs.");
            return new(Describe(root, candidate, policy.Platform), expectedTarget, true);
        }
        string work = Path.Combine(root, "versions", ".prepare-" + Guid.NewGuid().ToString("N"));
        bool published = false;
        try
        {
            var result = await PrepareVersion(work, root, archivePath, bytes, candidate, policy, token);
            using var selection = Lock(Path.Combine(manager, "activation.lock"));
            using var checkpoint = Lock(Path.Combine(root, "state/check.lock"));
            if (PosixUpdateActivation.InspectTarget(manager) != expectedTarget)
                throw new InvalidDataException("The Mac installation selection changed during registration.");
            await RequireAccepted(root, policy, baseline, candidate, token);
            token.ThrowIfCancellationRequested();
            RequireAbsent(destination);
            Directory.Move(work, destination);
            published = true;
            return new(result, expectedTarget, false);
        }
        finally { if (!published) MacUpdateStager.RemoveOwnedTree(work); }
    }

    public static async Task<InstalledMacUpdate> InspectSelectedAsync(string installationRoot, string expectedTarget, CancellationToken token = default)
    {
        string root = RootPath(installationRoot);
        var policy = await Policy(root, token);
        if (PosixUpdateActivation.InspectTarget(Path.Combine(root, "manager")) != expectedTarget)
            throw new InvalidDataException("The Mac selection changed before inspection.");
        return Describe(root, await ReadVersion(CurrentVersion(root, expectedTarget), policy, token), policy.Platform);
    }

    public static string InspectTarget(string root) => PosixUpdateActivation.InspectTarget(Path.Combine(RootPath(root), "manager"));

    public static async Task<InstalledMacUpdate> InspectExecutableVersionAsync(string installationRoot, string executable, CancellationToken token = default)
    {
        string root = RootPath(installationRoot);
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Use an absolute native executable path.");
        string relative = Path.GetRelativePath(Path.Combine(root, "versions"), Path.GetFullPath(executable));
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length != 7 || !Digest(parts[0]) || string.Join('/', parts.Skip(1)) != "payload/install/KiCad.app/Contents/MacOS/kicad")
            throw new InvalidDataException("The native Mac executable is outside the registered version store.");
        var policy = await Policy(root, token);
        return Describe(root, await ReadVersion(Path.Combine(root, "versions", parts[0]), policy, token), policy.Platform);
    }

    private static async Task<InstalledMacUpdate> PrepareVersion(string version, string finalRoot, string archive,
        byte[] envelope, VerifiedUpdateManifest manifest, MacInstallationPolicy policy, CancellationToken token)
    {
        RequireAbsent(version);
        Directory.CreateDirectory(version);
        var artifact = manifest.ForInstallation(policy.Platform, "tar.gz") ?? throw new InvalidDataException("Missing Mac target archive.");
        var staged = await MacUpdateStager.StageAsync(manifest, new DownloadedUpdate(archive, artifact, manifest.PayloadSha256), version, token);
        string stage = Path.GetDirectoryName(staged.Directory)!;
        string payload = Path.Combine(version, "payload");
        Directory.Move(staged.Directory, payload);
        File.Move(Path.Combine(stage, "staging.json"), Path.Combine(version, "staging.json"));
        File.Delete(Path.Combine(stage, "verified.tar.gz")); // Only the verified private duplicate; caller archive is preserved.
        Directory.Move(stage, Path.Combine(version, "diagnostics"));
        await WriteBytes(Path.Combine(version, "installed-envelope.json"), envelope, token);
        string finalVersion = Path.Combine(finalRoot, "versions", manifest.PayloadSha256);
        await WriteNew(Path.Combine(version, "update-config.json"), Configuration(finalRoot, finalVersion, policy), token);
        await WriteNew(Path.Combine(version, "version.json"), new MacVersionRegistration(1, manifest.PayloadSha256,
            manifest.PublisherKeySha256, await PosixPayloadFingerprint.ComputeAsync(payload, token)), token);
        return Describe(finalRoot, manifest, policy.Platform);
    }

    private static async Task<VerifiedUpdateManifest> ReadVersion(string version, MacInstallationPolicy policy, CancellationToken token)
    {
        if (new DirectoryInfo(version).LinkTarget is not null) throw new InvalidDataException("Registered Mac version cannot be an alias.");
        var manifest = UpdateManifestCodec.Verify(await ReadBytes(Path.Combine(version, "installed-envelope.json"), UpdateManifestCodec.MaximumEnvelopeBytes, token),
            Convert.FromBase64String(policy.PublisherKeySpki), policy.Channel);
        _ = manifest.ForInstallation(policy.Platform, "tar.gz") ?? throw new InvalidDataException("Registered Mac target differs.");
        var registration = await ReadJson<MacVersionRegistration>(Path.Combine(version, "version.json"), 4096, token);
        string root = Path.GetDirectoryName(Path.GetDirectoryName(version)!)!;
        var configuration = await ReadJson<UpdatePreparationConfiguration>(Path.Combine(version, "update-config.json"), 64 * 1024, token);
        if (registration.SchemaVersion != 1 || registration.ManifestSha256 != manifest.PayloadSha256
            || registration.PublisherKeySha256 != manifest.PublisherKeySha256 || Path.GetFileName(version) != manifest.PayloadSha256
            || configuration != Configuration(root, version, policy)
            || registration.PayloadSha256 != await PosixPayloadFingerprint.ComputeAsync(Path.Combine(version, "payload"), token))
            throw new InvalidDataException("The registered Mac version changed after verification.");
        await MacSignatureVerification.VerifyAsync(Path.Combine(version, "payload"), token);
        return manifest;
    }

    private static async Task RequireAccepted(string root, MacInstallationPolicy policy, VerifiedUpdateManifest baseline,
        VerifiedUpdateManifest candidate, CancellationToken token)
    {
        VerifiedUpdateManifest accepted;
        try { accepted = UpdateManifestCodec.Verify(await ReadBytes(Path.Combine(root, "state/accepted-envelope.json"),
            UpdateManifestCodec.MaximumEnvelopeBytes, token), Convert.FromBase64String(policy.PublisherKeySpki), policy.Channel); }
        catch (FileNotFoundException) { return; }
        if ((accepted.Release.Sequence == baseline.Release.Sequence && accepted.PayloadSha256 != baseline.PayloadSha256)
            || candidate.Release.Sequence < accepted.Release.Sequence
            || (candidate.Release.Sequence == accepted.Release.Sequence && candidate.PayloadSha256 != accepted.PayloadSha256))
            throw new InvalidDataException("Mac candidate replays or conflicts with accepted metadata.");
    }

    private static InstalledMacUpdate Describe(string root, VerifiedUpdateManifest manifest, string platform)
    {
        string version = Path.Combine(root, "versions", manifest.PayloadSha256);
        return new(root, Path.Combine(version, "payload"), Path.Combine(root, "manager"), Path.Combine(version, "update-config.json"),
            manifest.PayloadSha256, manifest.Release.Commit, platform);
    }
    private static UpdatePreparationConfiguration Configuration(string root, string version, MacInstallationPolicy policy) => new(1,
        policy.Origin, policy.PublisherKeySpki, Path.Combine(version, "installed-envelope.json"), Path.Combine(root, "state"),
        Path.Combine(root, "staging"), policy.Channel, policy.Platform, "tar.gz", root);

    private static string CurrentVersion(string root, string target)
    {
        string selections = Path.Combine(root, "manager/selections");
        string selection = Path.GetFullPath(Path.Combine(root, "manager", target));
        if (Path.GetDirectoryName(selection) != selections) throw new InvalidDataException("Selection is outside the Mac installation.");
        string link = new DirectoryInfo(selection).LinkTarget ?? throw new InvalidDataException("Mac selection is not a link.");
        string payload = Path.GetFullPath(Path.Combine(selections, link));
        string version = Path.GetDirectoryName(payload)!;
        if (Path.GetFileName(payload) != "payload" || Path.GetDirectoryName(version) != Path.Combine(root, "versions") || !Digest(Path.GetFileName(version)))
            throw new InvalidDataException("Selection is outside the registered Mac versions.");
        return version;
    }
    private static async Task<MacInstallationPolicy> Policy(string root, CancellationToken token)
    {
        var policy = await ReadJson<MacInstallationPolicy>(Path.Combine(root, "publisher.json"), 16 * 1024, token);
        if (policy.SchemaVersion != 1 || policy.Platform != Platform()) throw new InvalidDataException("Mac installation policy targets a different platform.");
        using var location = new UpdateDownloader(new Uri(policy.Origin, UriKind.Absolute));
        _ = UpdateManifestCodec.ValidatePublisherKey(Convert.FromBase64String(policy.PublisherKeySpki));
        return policy;
    }
    private static string RootPath(string root, bool exists = true)
    {
        _ = Platform();
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Use an absolute Mac installation path.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root == Path.GetPathRoot(root) || new DirectoryInfo(root).LinkTarget is not null)
            throw new ArgumentException("Use a dedicated ordinary Mac installation directory.");
        if (exists && !Directory.Exists(root)) throw new DirectoryNotFoundException("The Mac installation does not exist.");
        return root;
    }
    private static string Platform()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac installation requires native macOS.");
        return RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "osx-arm64", Architecture.X64 => "osx-x64",
            _ => throw new PlatformNotSupportedException("Unsupported Mac installation architecture.") };
    }
    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    private static FileStream Lock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static void RequireAbsent(string path)
    {
        if (Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Existing Mac installation data must not be overwritten: " + path);
    }
    private static async Task<byte[]> ReadBytes(string path, int maximum, CancellationToken token)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Mac installation metadata cannot be a symbolic link.");
        await using var file = File.OpenRead(path);
        if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("Mac installation metadata has an invalid size.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        return bytes;
    }
    private static async Task<T> ReadJson<T>(string path, int maximum, CancellationToken token) =>
        JsonSerializer.Deserialize<T>(await ReadBytes(path, maximum, token), Json) ?? throw new InvalidDataException("Missing Mac installation metadata.");
    private static Task WriteNew<T>(string path, T value, CancellationToken token) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json), token);
    private static async Task WriteBytes(string path, byte[] bytes, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, token);
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
    }
}
