using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Distribution;

public sealed record VerifiedWindowsVersion(string Root, string VersionDirectory, string ManifestSha256, string Commit)
{
    public string NativeExecutable => Path.Combine(VersionDirectory, "bin/kicad.exe");
    public string McpExecutable => Path.Combine(VersionDirectory, "bin/kicad-mcp.exe");
    public string UpdateConfiguration => Path.Combine(Path.GetDirectoryName(VersionDirectory)!, "update-config.json");
}
public sealed record RegisteredWindowsVersion(VerifiedWindowsVersion Version, string ExpectedSelectionId, bool Reused);
public sealed record SelectedWindowsVersion(VerifiedWindowsVersion Version, string SelectionId);
internal sealed record WindowsPublisherPolicy(int SchemaVersion, string Origin, string PublisherKeySpki, string Channel, string Platform);
internal sealed record WindowsVersionRegistration(int SchemaVersion, string ManifestSha256, string PublisherKeySha256, string PayloadSha256);

/// <summary>Authenticated retained versions for the Windows installer. This
/// store does not supply a launcher, native Update action, or editor restart.</summary>
public static partial class WindowsVerifiedVersions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 8 };

    public static async Task<SelectedWindowsVersion> CreateAsync(string root, string archive,
        ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> trustedPublisherSpki, Uri origin, string channel,
        CancellationToken token = default)
    {
        root = Root(root, exists: false);
        if (!Path.IsPathFullyQualified(archive)) throw new ArgumentException("Use an absolute archive path.");
        Absent(root);
        string parent = Path.GetDirectoryName(root)!;
        Ordinary(parent, directory: true);
        byte[] signed = envelope.ToArray(), key = trustedPublisherSpki.ToArray();
        var manifest = UpdateManifestCodec.Verify(signed, key, channel);
        _ = manifest.ForInstallation("win-x64", "zip") ?? throw new InvalidDataException("No Windows x64 archive is declared.");
        using var location = new UpdateDownloader(origin);
        var policy = new WindowsPublisherPolicy(1, origin.AbsoluteUri, Convert.ToBase64String(key), channel, "win-x64");
        string work = Path.Combine(parent, ".kicad-windows-store-" + Guid.NewGuid().ToString("N"));
        Absent(work); token.ThrowIfCancellationRequested(); Directory.CreateDirectory(work);
        bool published = false;
        try
        {
            await WriteNew(Path.Combine(work, "publisher.json"), policy, token);
            foreach (string name in new[] { "versions", "state", "staging" }) Directory.CreateDirectory(Path.Combine(work, name));
            await Prepare(Path.Combine(work, "versions", manifest.PayloadSha256), Path.Combine(work, "staging"), root,
                policy, archive, signed, manifest, token);
            var selection = await WindowsVersionSelection.InitializeAsync(Path.Combine(work, "manager"), manifest.PayloadSha256, token);
            await WriteNew(Path.Combine(work, "store.json"), new
            { schemaVersion = 1, status = "verified_version_store", installationReady = false, nativeEditorRestarted = false }, token);
            token.ThrowIfCancellationRequested(); Absent(root); Directory.Move(work, root); published = true;
            return new(Describe(root, manifest), selection.SelectionId);
        }
        finally
        {
            if (!published) await RetainFailureAndRemove(work, parent, token: CancellationToken.None);
        }
    }

    public static async Task<RegisteredWindowsVersion> RegisterAsync(string root, string expectedSelectionId,
        string archive, ReadOnlyMemory<byte> envelope, CancellationToken token = default)
    {
        root = Root(root); RequireId(expectedSelectionId);
        if (!Path.IsPathFullyQualified(archive)) throw new ArgumentException("Use an absolute archive path.");
        var policy = await Policy(root, token);
        using var registration = Lock(Path.Combine(root, "registration.lock"));
        var selected = WindowsVersionSelection.Inspect(Path.Combine(root, "manager"));
        if (selected.SelectionId != expectedSelectionId) throw new InvalidDataException("Windows selection changed before registration.");
        var baseline = await ReadVersion(root, DigestOf(selected), policy, token);
        byte[] signed = envelope.ToArray();
        var candidate = UpdateManifestCodec.Verify(signed, Convert.FromBase64String(policy.PublisherKeySpki), policy.Channel,
            new(baseline.Release.Sequence, baseline.PayloadSha256));
        await RequireAccepted(root, policy, baseline, candidate, token);
        string destination = Path.Combine(root, "versions", candidate.PayloadSha256);
        if (Directory.Exists(destination))
        {
            var existing = await ReadVersion(root, candidate.PayloadSha256, policy, token);
            return new(Describe(root, existing), expectedSelectionId, true);
        }
        string work = Path.Combine(root, "versions", ".prepare-" + Guid.NewGuid().ToString("N"));
        bool published = false;
        try
        {
            await Prepare(work, Path.Combine(root, "staging"), root, policy, archive, signed, candidate, token);
            using var selectionLock = Lock(Path.Combine(root, "manager/activation.lock"));
            using var checkpoint = Lock(Path.Combine(root, "state/check.lock"));
            if (WindowsVersionSelection.Inspect(Path.Combine(root, "manager")) != selected)
                throw new InvalidDataException("Windows selection changed while the candidate was prepared.");
            await RequireAccepted(root, policy, baseline, candidate, token);
            token.ThrowIfCancellationRequested(); Absent(destination); Directory.Move(work, destination); published = true;
            return new(Describe(root, candidate), expectedSelectionId, false);
        }
        finally { if (!published) await RetainFailureAndRemove(work, Path.Combine(root, "staging"), CancellationToken.None); }
    }

    public static async Task<SelectedWindowsVersion> InspectCurrentAsync(string root, CancellationToken token = default)
    {
        root = Root(root); var policy = await Policy(root, token);
        using var registration = Lock(Path.Combine(root, "registration.lock"));
        var selected = WindowsVersionSelection.Inspect(Path.Combine(root, "manager"));
        var version = await ReadVersion(root, DigestOf(selected), policy, token);
        if (WindowsVersionSelection.Inspect(Path.Combine(root, "manager")) != selected)
            throw new InvalidDataException("Windows selection changed during inspection.");
        return new(Describe(root, version), selected.SelectionId);
    }

    public static string InspectSelectionId(string root) => WindowsVersionSelection.Inspect(Path.Combine(Root(root), "manager")).SelectionId;

    public static async Task ValidateUpdateConfigurationAsync(UpdatePreparationConfiguration configuration, CancellationToken token = default)
    {
        string root = Root(configuration.InstallationRoot ?? throw new InvalidDataException("The Windows configuration is not store-bound."));
        if (!Path.IsPathFullyQualified(configuration.InstalledEnvelope)) throw new InvalidDataException("The Windows installed envelope path is not absolute.");
        string[] parts = Path.GetRelativePath(Path.Combine(root, "versions"), configuration.InstalledEnvelope).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 2 || !Digest(parts[0]) || parts[1] != "installed-envelope.json")
            throw new InvalidDataException("The Windows configuration does not identify a retained version.");
        var policy = await Policy(root, token);
        if (configuration != Configuration(root, parts[0], policy)) throw new InvalidDataException("The Windows update configuration changed after registration.");
        _ = await ReadVersion(root, parts[0], policy, token);
    }

    public static async Task<VerifiedWindowsVersion> InspectExecutableAsync(string root, string executable, CancellationToken token = default)
    {
        root = Root(root);
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Use an absolute native executable path.");
        string[] parts = Path.GetRelativePath(Path.Combine(root, "versions"), Path.GetFullPath(executable)).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 4 || !Digest(parts[0]) || parts[1] != "payload" || parts[2] != "bin"
            || !string.Equals(parts[3], "kicad.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The executable is outside the retained Windows versions.");
        return Describe(root, await ReadVersion(root, parts[0], await Policy(root, token), token));
    }

    private static async Task Prepare(string version, string stagingRoot, string finalRoot, WindowsPublisherPolicy policy,
        string archive, byte[] envelope, VerifiedUpdateManifest manifest, CancellationToken token)
    {
        Absent(version); Directory.CreateDirectory(version);
        var artifact = manifest.ForInstallation("win-x64", "zip") ?? throw new InvalidDataException("Windows archive missing.");
        // Native Windows launch paths must not accumulate temporary UUIDs below
        // a 64-character version digest. Staging is a sibling on this volume.
        var staged = await WindowsUpdateStager.StageAsync(manifest, new(archive, artifact, manifest.PayloadSha256), stagingRoot, token);
        string stage = Path.GetDirectoryName(staged.Directory)!;
        try
        {
            string payload = Path.Combine(version, "payload");
            Directory.Move(staged.Directory, payload);
            File.Move(Path.Combine(stage, "staging.json"), Path.Combine(version, "staging.json"));
            File.Delete(Path.Combine(stage, "verified.zip")); // Private verified duplicate; never the caller's archive.
            Directory.Move(stage, Path.Combine(version, "diagnostics"));
            await WriteBytes(Path.Combine(version, "installed-envelope.json"), envelope, token);
            await WriteNew(Path.Combine(version, "update-config.json"), Configuration(finalRoot, manifest.PayloadSha256, policy), token);
            await WriteNew(Path.Combine(version, "version.json"), new WindowsVersionRegistration(1, manifest.PayloadSha256,
                manifest.PublisherKeySha256, await WindowsPayloadFingerprint.ComputeAsync(payload, token)), token);
        }
        finally { if (Directory.Exists(stage)) await RetainFailureAndRemove(stage, stagingRoot, CancellationToken.None); }
    }

    private static async Task<VerifiedUpdateManifest> ReadVersion(string root, string digest, WindowsPublisherPolicy policy, CancellationToken token)
    {
        if (!Digest(digest)) throw new InvalidDataException("Invalid retained Windows digest.");
        Ordinary(Path.Combine(root, "versions"), directory: true);
        string version = Path.Combine(root, "versions", digest); Ordinary(version, directory: true);
        var manifest = UpdateManifestCodec.Verify(await ReadBytes(Path.Combine(version, "installed-envelope.json"), UpdateManifestCodec.MaximumEnvelopeBytes, token),
            Convert.FromBase64String(policy.PublisherKeySpki), policy.Channel);
        _ = manifest.ForInstallation("win-x64", "zip") ?? throw new InvalidDataException("Registered Windows target differs.");
        var registration = await ReadJson<WindowsVersionRegistration>(Path.Combine(version, "version.json"), 4096, token);
        var configuration = await ReadJson<UpdatePreparationConfiguration>(Path.Combine(version, "update-config.json"), 65536, token);
        if (registration.SchemaVersion != 1 || manifest.PayloadSha256 != digest || registration.ManifestSha256 != digest
            || registration.PublisherKeySha256 != manifest.PublisherKeySha256
            || configuration != Configuration(root, digest, policy)
            || registration.PayloadSha256 != await WindowsPayloadFingerprint.ComputeAsync(Path.Combine(version, "payload"), token))
            throw new InvalidDataException("The registered Windows version changed after verification.");
        return manifest;
    }

    private static async Task RequireAccepted(string root, WindowsPublisherPolicy policy, VerifiedUpdateManifest baseline,
        VerifiedUpdateManifest candidate, CancellationToken token)
    {
        VerifiedUpdateManifest accepted;
        try { accepted = UpdateManifestCodec.Verify(await ReadBytes(Path.Combine(root, "state/accepted-envelope.json"), UpdateManifestCodec.MaximumEnvelopeBytes, token),
            Convert.FromBase64String(policy.PublisherKeySpki), policy.Channel); }
        catch (FileNotFoundException) { return; }
        if ((accepted.Release.Sequence == baseline.Release.Sequence && accepted.PayloadSha256 != baseline.PayloadSha256)
            || candidate.Release.Sequence < accepted.Release.Sequence
            || (candidate.Release.Sequence == accepted.Release.Sequence && candidate.PayloadSha256 != accepted.PayloadSha256))
            throw new InvalidDataException("Windows candidate replays or conflicts with accepted update metadata.");
    }

    private static VerifiedWindowsVersion Describe(string root, VerifiedUpdateManifest manifest) =>
        new(root, Path.Combine(root, "versions", manifest.PayloadSha256, "payload"), manifest.PayloadSha256, manifest.Release.Commit);
    private static UpdatePreparationConfiguration Configuration(string root, string digest, WindowsPublisherPolicy policy) => new(1,
        policy.Origin, policy.PublisherKeySpki, Path.Combine(root, "versions", digest, "installed-envelope.json"),
        Path.Combine(root, "state"), Path.Combine(root, "staging"), policy.Channel, "win-x64", "zip", root);
    private static string DigestOf(WindowsSelectedVersion selected) => selected.VersionTarget[9..73];
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static void RequireId(string id)
    { if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty) throw new ArgumentException("An exact Windows selection identity is required."); }
    private static string Root(string root, bool exists = true)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Windows x64 execution is required.");
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("Use a local absolute version-store path.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root == Path.GetPathRoot(root)) throw new ArgumentException("A filesystem root is not a version store.");
        if (exists) Ordinary(root, directory: true);
        return root;
    }
    private static async Task<WindowsPublisherPolicy> Policy(string root, CancellationToken token)
    {
        var policy = await ReadJson<WindowsPublisherPolicy>(Path.Combine(root, "publisher.json"), 16384, token);
        if (policy.SchemaVersion != 1 || policy.Platform != "win-x64") throw new InvalidDataException("Windows publisher policy targets another platform.");
        using var location = new UpdateDownloader(new Uri(policy.Origin, UriKind.Absolute));
        _ = UpdateManifestCodec.ValidatePublisherKey(Convert.FromBase64String(policy.PublisherKeySpki));
        return policy;
    }
    private static void Ordinary(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new InvalidDataException("Windows version-store entries cannot be redirected or have the wrong kind.");
    }
    private static void Absent(string path)
    {
        if (Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Existing Windows version-store data must not be overwritten: " + path);
    }
    private static FileStream Lock(string path)
    {
        if (File.Exists(path)) Ordinary(path, directory: false);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static async Task<byte[]> ReadBytes(string path, int maximum, CancellationToken token)
    {
        Ordinary(path, directory: false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("Invalid Windows metadata size.");
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token); return bytes;
    }
    private static async Task<T> ReadJson<T>(string path, int maximum, CancellationToken token)
    {
        byte[] bytes = await ReadBytes(path, maximum, token);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
            throw new InvalidDataException("Repeated Windows metadata property.");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing Windows metadata.");
    }
    private static Task WriteNew<T>(string path, T data, CancellationToken token) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(data, Json), token);
    private static async Task WriteBytes(string path, byte[] bytes, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(flushToDisk: true);
    }
    private static async Task RetainFailureAndRemove(string work, string parent, CancellationToken token)
    {
        if (!Directory.Exists(work)) return;
        string? diagnostics = null;
        foreach (string file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) == "failure.json" || Path.GetExtension(path) == ".log"))
        {
            diagnostics ??= Directory.CreateDirectory(Path.Combine(parent, ".windows-version-diagnostics-" + Guid.NewGuid().ToString("N"))).FullName;
            string target = Path.Combine(diagnostics, Path.GetRelativePath(work, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file); await using var output = File.Create(target);
            await input.CopyToAsync(output, token);
        }
        Directory.Delete(work, recursive: true);
    }
}
