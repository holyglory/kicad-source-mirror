using System.Security.Cryptography;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Downloads;

public sealed class SignedUpdateFeed
{
    private readonly byte[] envelope;
    public string Sha256 { get; }
    public VerifiedUpdateManifest Manifest { get; }
    internal SignedUpdateFeed(byte[] bytes, VerifiedUpdateManifest manifest)
    { envelope = (byte[])bytes.Clone(); Manifest = manifest; Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
    public byte[] CopyEnvelope() => (byte[])envelope.Clone();
}

/// <summary>Public, immutable signed metadata only. No private signing key is
/// loaded, no release is signed by the web process, and no upload API exists.</summary>
public sealed class SignedUpdateCatalogue
{
    private readonly Dictionary<string, SignedUpdateFeed> feeds = new(StringComparer.Ordinal);
    public static SignedUpdateCatalogue Empty => new();
    public bool TryGet(string channel, out SignedUpdateFeed? feed) => feeds.TryGetValue(channel, out feed);
    public bool TryGet(string platform, string channel, out SignedUpdateFeed? feed)
    {
        feed = null;
        return PlatformUpdateFeeds.IsNativePlatform(platform) && feeds.TryGetValue(platform + "/" + channel, out feed);
    }

    public static async Task<SignedUpdateCatalogue> LoadAsync(string root, string publisherSpkiPath,
        DownloadCatalogue downloads, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(publisherSpkiPath))
            throw new ArgumentException("Use absolute update metadata and publisher public-key paths.");
        byte[] key = await ReadFileAsync(publisherSpkiPath, 4096, token);
        _ = UpdateManifestCodec.ValidatePublisherKey(key);
        var result = new SignedUpdateCatalogue();
        RequireOrdinaryDirectory(Path.Combine(root, "updates"));
        foreach (string channel in new[] { "preview", "stable" })
        {
            await LoadFeed(Path.Combine(root, "updates", channel + ".json"), channel, null);
            foreach (string platform in PlatformUpdateFeeds.NativePlatforms)
            {
                RequireOrdinaryDirectory(Path.Combine(root, "updates/platforms"));
                RequireOrdinaryDirectory(Path.Combine(root, "updates/platforms", platform));
                await LoadFeed(Path.Combine(root, PlatformUpdateFeeds.RelativeFeed(platform, channel)), channel, platform);
            }
        }
        return result;

        async Task LoadFeed(string path, string channel, string? platform)
        {
            if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("An update feed cannot be a link.");
            if (!File.Exists(path)) return;
            byte[] bytes = await ReadFileAsync(path, UpdateManifestCodec.MaximumEnvelopeBytes, token);
            var verified = UpdateManifestCodec.Verify(bytes, key, channel);
            if (platform is null) PlatformUpdateFeeds.RequireLegacyCompatible(verified);
            else if (verified.Release.Artifacts.Any(x => x.Platform != platform))
                throw new InvalidDataException("A native feed contains an artifact for a different platform.");
            foreach (var artifact in verified.Release.Artifacts)
            {
                if (!downloads.Files.TryGetValue(artifact.FileName, out var published)
                    || published.Artifact.Platform != artifact.Platform || published.Artifact.Bytes != artifact.Bytes
                    || published.Artifact.Sha256 != artifact.Sha256 || published.Artifact.Commit != verified.Release.Commit
                    || published.Artifact.Version != verified.Release.Version)
                    throw new InvalidDataException("Signed update metadata does not match the published package catalogue.");
            }
            result.feeds.Add(platform is null ? channel : platform + "/" + channel, new(bytes, verified));
        }
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path))
            throw new InvalidDataException("Update feed directories must be ordinary directories.");
    }

    private static async Task<byte[]> ReadFileAsync(string path, int maximum, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 || info.Length > maximum)
            throw new InvalidDataException("Update metadata must be a bounded, regular, non-linked file.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[(int)input.Length];
        if (bytes.Length > maximum) throw new InvalidDataException("Update metadata changed during reading.");
        await input.ReadExactlyAsync(bytes, token);
        return bytes;
    }
}
