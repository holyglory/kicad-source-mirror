using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Distribution;

internal sealed record LinuxInstallationPolicy(int SchemaVersion, string Origin, string PublisherKeySpki, string Channel);
internal sealed record LinuxVersionRegistration(int SchemaVersion, string ManifestSha256, string PublisherKeySha256, string PayloadSha256);
public sealed record RegisteredLinuxUpdate(InstalledLinuxUpdate Version, string ExpectedTarget, bool Reused);

public static partial class LinuxVerifiedInstallation
{
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    public static async Task<InstalledLinuxUpdate> InspectSelectedAsync(string root, string expectedTarget, CancellationToken token = default)
    {
        root = ActivationRoot(root, Guid.NewGuid());
        var policy = await ActivationPolicyAsync(root, token);
        if (LinuxUpdateActivation.InspectTarget(Path.Combine(root, "manager")) != expectedTarget)
            throw new InvalidDataException("The active installation changed before inspection.");
        var manifest = await ReadVersionAsync(CurrentVersion(root, expectedTarget),
            Convert.FromBase64String(policy.PublisherKeySpki), policy, token);
        return DescribeVersion(root, manifest);
    }

    /// <summary>Verifies the exact retained version a live process runs. It
    /// need not be the selection now used for future launches: another project
    /// instance may already have updated the installation.</summary>
    public static async Task<InstalledLinuxUpdate> InspectExecutableVersionAsync(string root, string executable,
        CancellationToken token = default)
    {
        root = ActivationRoot(root, Guid.NewGuid());
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Use an absolute native executable path.");
        string versions = Path.Combine(root, "versions");
        string relative = Path.GetRelativePath(versions, Path.GetFullPath(executable));
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length != 5 || !DigestName(parts[0]) || parts[1] != "payload" || parts[2] != "runtime"
            || parts[3] != "bin" || parts[4] != "kicad")
            throw new InvalidDataException("The process executable is outside this installation's registered version store.");
        var policy = await ActivationPolicyAsync(root, token);
        var manifest = await ReadVersionAsync(Path.Combine(versions, parts[0]),
            Convert.FromBase64String(policy.PublisherKeySpki), policy, token);
        return DescribeVersion(root, manifest);
    }

    /// <summary>Registers authenticated candidate bytes without switching the
    /// active installation. Publisher identity comes only from the installed
    /// root policy, never from the candidate or the calling request.</summary>
    public static async Task<RegisteredLinuxUpdate> RegisterAsync(string root, string expectedTarget,
        string archivePath, ReadOnlyMemory<byte> envelope, CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux registration requires Linux.");
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(archivePath))
            throw new ArgumentException("Use absolute installation and archive paths.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        token.ThrowIfCancellationRequested();
        var policy = await ReadJsonAsync<LinuxInstallationPolicy>(Path.Combine(root, "publisher.json"), 16 * 1024, token);
        if (policy.SchemaVersion != 1) throw new InvalidDataException("Unsupported installation publisher policy.");
        byte[] key = Convert.FromBase64String(policy.PublisherKeySpki);
        using var source = new UpdateDownloader(new Uri(policy.Origin, UriKind.Absolute));
        using var registrationLock = new FileStream(Path.Combine(root, "registration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string manager = Path.Combine(root, "manager");
        string current = LinuxUpdateActivation.InspectTarget(manager);
        if (current != expectedTarget) throw new InvalidDataException("The active installation changed before registration.");
        string selected = CurrentVersion(root, current);
        var baseline = await ReadVersionAsync(selected, key, policy, token);
        byte[] bytes = envelope.ToArray();
        var candidate = UpdateManifestCodec.Verify(bytes, key, policy.Channel,
            new(baseline.Release.Sequence, baseline.PayloadSha256));
        await RequireAcceptedSequenceAsync(root, key, policy.Channel, baseline, candidate, token);
        string destination = Path.Combine(root, "versions", candidate.PayloadSha256);
        if (Directory.Exists(destination))
        {
            var existing = await ReadVersionAsync(destination, key, policy, token);
            if (existing.PayloadSha256 != candidate.PayloadSha256)
                throw new InvalidDataException("Registered candidate identity does not match.");
            return new(DescribeVersion(root, candidate), expectedTarget, true);
        }
        RequireAbsent(destination);
        string work = Path.Combine(root, "versions", ".prepare-" + Guid.NewGuid().ToString("N"));
        bool registered = false;
        try
        {
            var result = await PrepareVersionAsync(work, root, archivePath, bytes, key, candidate, policy, token);
            token.ThrowIfCancellationRequested();
            // Serialize only the commit boundary with activation. Extraction
            // and validation never keep a native selection lock for minutes.
            using var activationLock = new FileStream(Path.Combine(manager, "activation.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            using var checkpointLock = new FileStream(Path.Combine(root, "state", "check.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            if (LinuxUpdateActivation.InspectTarget(manager) != expectedTarget)
                throw new InvalidDataException("The active installation changed during registration.");
            await RequireAcceptedSequenceAsync(root, key, policy.Channel, baseline, candidate, token);
            RequireAbsent(destination);
            Directory.Move(work, destination);
            registered = true;
            return new(result, expectedTarget, false);
        }
        finally { if (!registered && Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    private static async Task RequireAcceptedSequenceAsync(string root, byte[] key, string channel,
        VerifiedUpdateManifest installed, VerifiedUpdateManifest candidate, CancellationToken token)
    {
        VerifiedUpdateManifest accepted;
        try
        {
            await using var file = File.OpenRead(Path.Combine(root, "state", "accepted-envelope.json"));
            accepted = UpdateManifestCodec.Verify(await UpdateDownloader.ReadEnvelopeAsync(file, token), key, channel);
        }
        catch (FileNotFoundException) { return; }
        if (accepted.Release.Sequence == installed.Release.Sequence && accepted.PayloadSha256 != installed.PayloadSha256)
            throw new InvalidDataException("Accepted update metadata conflicts with the installed release.");
        if (candidate.Release.Sequence < accepted.Release.Sequence
            || candidate.Release.Sequence == accepted.Release.Sequence && candidate.PayloadSha256 != accepted.PayloadSha256)
            throw new InvalidDataException("Candidate registration would replay older or conflicting accepted metadata.");
    }

    private static InstalledLinuxUpdate DescribeVersion(string root, VerifiedUpdateManifest manifest)
    {
        string version = Path.Combine(root, "versions", manifest.PayloadSha256);
        return new(root, Path.Combine(version, "payload"), Path.Combine(root, "manager"),
            Path.Combine(version, "update-config.json"), manifest.PayloadSha256, manifest.Release.Commit);
    }

    private static string CurrentVersion(string root, string current)
    {
        string selections = Path.Combine(root, "manager", "selections") + Path.DirectorySeparatorChar;
        string selection = Path.GetFullPath(Path.Combine(root, "manager", current));
        if (!selection.StartsWith(selections, StringComparison.Ordinal)
            || Path.GetDirectoryName(selection) != Path.TrimEndingDirectorySeparator(selections))
            throw new InvalidDataException("The current selection is outside the managed directory.");
        string versionTarget = new DirectoryInfo(selection).LinkTarget
            ?? throw new InvalidDataException("The current selection has no version link.");
        string payload = Path.GetFullPath(Path.Combine(selections, versionTarget));
        string version = Path.GetDirectoryName(payload)!;
        if (Path.GetFileName(payload) != "payload" || Path.GetDirectoryName(version) != Path.Combine(root, "versions")
            || !DigestName(Path.GetFileName(version)))
            throw new InvalidDataException("The current version is outside the registered version store.");
        return version;
    }

    private static async Task<VerifiedUpdateManifest> ReadVersionAsync(string directory, byte[] key, LinuxInstallationPolicy policy, CancellationToken token)
    {
        byte[] envelope;
        await using (var file = File.OpenRead(Path.Combine(directory, "installed-envelope.json")))
            envelope = await UpdateDownloader.ReadEnvelopeAsync(file, token);
        var manifest = UpdateManifestCodec.Verify(envelope, key, policy.Channel);
        var registration = await ReadJsonAsync<LinuxVersionRegistration>(Path.Combine(directory, "version.json"), 4096, token);
        string root = Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!;
        var configuration = await ReadJsonAsync<UpdatePreparationConfiguration>(Path.Combine(directory, "update-config.json"), 64 * 1024, token);
        var expectedConfiguration = new UpdatePreparationConfiguration(1, policy.Origin, policy.PublisherKeySpki,
            Path.Combine(directory, "installed-envelope.json"), Path.Combine(root, "state"), Path.Combine(root, "staging"),
            policy.Channel, "linux-x64", "tar.gz", root);
        if (registration.SchemaVersion != 1 || registration.ManifestSha256 != manifest.PayloadSha256
            || registration.PublisherKeySha256 != manifest.PublisherKeySha256
            || Path.GetFileName(directory) != manifest.PayloadSha256
            || configuration != expectedConfiguration
            || registration.PayloadSha256 != await LinuxPayloadFingerprint.ComputeAsync(Path.Combine(directory, "payload"), token))
            throw new InvalidDataException("The registered version changed after verification.");
        return manifest;
    }

    private static bool DigestName(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<T> ReadJsonAsync<T>(string path, int maximum, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("Invalid installation metadata size.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || json.RootElement.EnumerateObject().GroupBy(item => item.Name).Any(group => group.Count() != 1))
            throw new InvalidDataException("Invalid or repeated installation metadata property.");
        return JsonSerializer.Deserialize<T>(bytes, StrictJson) ?? throw new InvalidDataException("Missing installation metadata.");
    }
}
