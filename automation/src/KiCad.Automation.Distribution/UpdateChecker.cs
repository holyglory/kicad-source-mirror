namespace KiCad.Automation.Distribution;

public enum UpdateAvailability { UpToDate, Available, TargetUnavailable }
public sealed record UpdateCheckResult(UpdateAvailability Availability, VerifiedUpdateManifest Manifest);

/// <summary>Checks one installed target against its pinned publisher and saves
/// authenticated metadata for replay protection across restarts. A successful
/// check is not a download, installation-ready state, or permission to restart.</summary>
public sealed class UpdateChecker
{
    private readonly UpdateDownloader source;
    private readonly string root, channel, platform, format;
    private readonly byte[] publisherKey;
    private readonly VerifiedUpdateManifest installed;

    // source remains owned by the caller. The public key comes from the trusted
    // application. installedEnvelope is its verified installation receipt,
    // written by the installer after authenticating the containing archive; a
    // package cannot embed a manifest containing its own final archive hash.
    // Never substitute the latest feed for that installed-version receipt.
    public UpdateChecker(UpdateDownloader source, string stateDirectory, ReadOnlySpan<byte> trustedPublisherSpki,
        ReadOnlyMemory<byte> installedEnvelope, string channel, string platform, string format)
    {
        if (!Path.IsPathFullyQualified(stateDirectory) || !Directory.Exists(stateDirectory))
            throw new ArgumentException("Use an existing dedicated update state directory.", nameof(stateDirectory));
        installed = UpdateManifestCodec.Verify(installedEnvelope, trustedPublisherSpki, channel);
        if (installed.ForInstallation(platform, format) is null)
            throw new InvalidDataException("Installed manifest does not describe this exact target.");
        this.source = source;
        root = stateDirectory;
        publisherKey = trustedPublisherSpki.ToArray();
        this.channel = channel;
        this.platform = platform;
        this.format = format;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Retain the lock file after closing it. Unlinking an open Unix lock file
        // would permit a second updater to lock a different inode at this path.
        // Independent editors share this checkpoint. Wait for its owner, then
        // reread accepted metadata so a queued check cannot lower the baseline.
        using var ownership = await UpdateStoreLease.AcquireAsync(Path.Combine(root, "check.lock"), cancellationToken);
        string acceptedPath = Path.Combine(root, "accepted-envelope.json");
        VerifiedUpdateManifest? saved = null;
        try
        {
            await using var file = new FileStream(acceptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            saved = UpdateManifestCodec.Verify(await UpdateDownloader.ReadEnvelopeAsync(file, cancellationToken),
                publisherKey, channel);
        }
        catch (FileNotFoundException) { }

        var baseline = installed;
        if (saved is not null && saved.Release.Sequence >= installed.Release.Sequence)
        {
            if (saved.Release.Sequence == installed.Release.Sequence && saved.PayloadSha256 != installed.PayloadSha256)
                throw new InvalidDataException("Saved update metadata conflicts with the installed release.");
            baseline = saved;
        }

        byte[] envelope = await source.FetchManifestAsync(channel, cancellationToken);
        var verified = UpdateManifestCodec.Verify(envelope, publisherKey, channel,
            new(baseline.Release.Sequence, baseline.PayloadSha256));
        cancellationToken.ThrowIfCancellationRequested();
        if (saved?.PayloadSha256 != verified.PayloadSha256)
        {
            string pending = Path.Combine(root, "accepted-" + Guid.NewGuid().ToString("N") + ".partial");
            bool ownsPending = false;
            try
            {
                await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsPending = true;
                    await file.WriteAsync(envelope, cancellationToken);
                    await file.FlushAsync(cancellationToken);
                    file.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pending, acceptedPath, overwrite: true);
                ownsPending = false;
            }
            finally { if (ownsPending) File.Delete(pending); }
        }
        var availability = verified.Release.Sequence == installed.Release.Sequence
            ? UpdateAvailability.UpToDate
            : verified.ForInstallation(platform, format) is not null
                ? UpdateAvailability.Available : UpdateAvailability.TargetUnavailable;
        return new(availability, verified);
    }
}
