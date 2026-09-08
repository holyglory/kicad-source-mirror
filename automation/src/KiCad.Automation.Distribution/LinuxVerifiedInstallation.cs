using System.Runtime.InteropServices;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

public sealed record InstalledLinuxUpdate(string Root, string VersionDirectory, string ManagerDirectory,
    string ConfigurationPath, string ManifestSha256, string Commit);

/// <summary>Creates a new, verified, per-operator installation. Does not replace
/// an existing root, restart editors, register desktop icons or modify system
/// packages. The publisher key is supplied through the trusted bootstrap, not
/// accepted from an online feed.</summary>
public static class LinuxVerifiedInstallation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Task<InstalledLinuxUpdate> InstallAsync(string installationRoot, string archivePath,
        ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> trustedPublisherSpki, Uri origin, string channel,
        CancellationToken token = default) =>
        InstallAsync(installationRoot, archivePath, envelope, trustedPublisherSpki, origin, channel, null, token);

    internal static async Task<InstalledLinuxUpdate> InstallAsync(string installationRoot, string archivePath,
        ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> trustedPublisherSpki, Uri origin, string channel,
        Action? beforePublish, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Verified Linux installation requires Linux x64.");
        if (!Path.IsPathFullyQualified(installationRoot) || !Path.IsPathFullyQualified(archivePath))
            throw new ArgumentException("Installation and archive paths must be absolute.");
        installationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot));
        string parent = Path.GetDirectoryName(installationRoot)
            ?? throw new ArgumentException("Use a new installation directory, not a filesystem root.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The installation parent directory is missing.");
        RequireAbsent(installationRoot);
        token.ThrowIfCancellationRequested();
        // Snapshot caller-owned buffers before asynchronous work or persistence.
        byte[] signedEnvelope = envelope.ToArray(), publisherKey = trustedPublisherSpki.ToArray();
        var manifest = UpdateManifestCodec.Verify(signedEnvelope, publisherKey, channel);
        var artifact = manifest.ForInstallation("linux-x64", "tar.gz")
            ?? throw new InvalidDataException("The signed release has no Linux x64 archive.");
        using var publisherLocation = new UpdateDownloader(origin); // validates HTTPS origin; makes no request
        string work = Path.Combine(parent, ".kicad-install-" + Guid.NewGuid().ToString("N"));
        RequireAbsent(work);
        Directory.CreateDirectory(work);
        bool published = false;
        try
        {
            string versionRelative = Path.Combine("versions", manifest.PayloadSha256);
            string version = Directory.CreateDirectory(Path.Combine(work, versionRelative)).FullName;
            var downloaded = new DownloadedUpdate(archivePath, artifact, manifest.PayloadSha256);
            var staged = await LinuxUpdateStager.StageAsync(manifest, downloaded, version, token);
            _ = await LinuxUpdateRuntime.CheckAsync(manifest, staged, work, token);
            string payload = Path.Combine(version, "payload");
            Directory.Move(staged.Directory, payload);
            // Keep the original staging receipt, including its explicit limits.
            File.Move(Path.Combine(Path.GetDirectoryName(staged.Directory)!, "staging.json"), Path.Combine(version, "staging.json"));
            Directory.Delete(Path.GetDirectoryName(staged.Directory)!); // now empty, created by this invocation

            string receiptRelative = Path.Combine(versionRelative, "installed-envelope.json");
            await WriteBytesAsync(Path.Combine(work, receiptRelative), signedEnvelope, token);
            Directory.CreateDirectory(Path.Combine(work, "state"));
            Directory.CreateDirectory(Path.Combine(work, "staging"));
            await LinuxUpdateActivation.InitializeAsync(Path.Combine(work, "manager"), payload, token);

            string finalVersion = Path.Combine(installationRoot, versionRelative);
            string configurationRelative = Path.Combine(versionRelative, "update-config.json");
            var configuration = new UpdatePreparationConfiguration(1, origin.AbsoluteUri,
                Convert.ToBase64String(publisherKey), Path.Combine(installationRoot, receiptRelative),
                Path.Combine(installationRoot, "state"), Path.Combine(installationRoot, "staging"),
                channel, "linux-x64", "tar.gz");
            await WriteBytesAsync(Path.Combine(work, configurationRelative), JsonSerializer.SerializeToUtf8Bytes(configuration, Json), token);
            var result = new InstalledLinuxUpdate(installationRoot, Path.Combine(finalVersion, "payload"),
                Path.Combine(installationRoot, "manager"), Path.Combine(installationRoot, configurationRelative),
                manifest.PayloadSha256, manifest.Release.Commit);
            await WriteBytesAsync(Path.Combine(work, "installed.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, status = "installed", result,
                preparedAtUtc = DateTimeOffset.UtcNow, nativeIdentityVerified = true,
                automaticUpdatingQualified = false, nativeEditorRestarted = false
            }, Json), token);
            beforePublish?.Invoke();
            token.ThrowIfCancellationRequested();
            RequireAbsent(installationRoot);
            // Work is a sibling on the same filesystem. No existing destination
            // is overwritten; cancellation before this boundary leaves no root.
            Directory.Move(work, installationRoot);
            published = true;
            return result;
        }
        finally
        {
            if (!published && Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static void RequireAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("The new installation destination already exists: " + path);
    }

    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, token);
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
    }
}
