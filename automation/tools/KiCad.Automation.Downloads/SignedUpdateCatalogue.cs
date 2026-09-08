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

    public static async Task<SignedUpdateCatalogue> LoadAsync(string root, string publisherSpkiPath,
        DownloadCatalogue downloads, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(publisherSpkiPath))
            throw new ArgumentException("Use absolute update metadata and publisher public-key paths.");
        byte[] key = await ReadFileAsync(publisherSpkiPath, 4096, token);
        _ = UpdateManifestCodec.ValidatePublisherKey(key);
        var result = new SignedUpdateCatalogue();
        foreach (string channel in new[] { "preview", "stable" })
        {
            string path = Path.Combine(root, "updates", channel + ".json");
            if (!File.Exists(path)) continue;
            byte[] bytes = await ReadFileAsync(path, UpdateManifestCodec.MaximumEnvelopeBytes, token);
            var verified = UpdateManifestCodec.Verify(bytes, key, channel);
            foreach (var artifact in verified.Release.Artifacts)
            {
                if (!downloads.Files.TryGetValue(artifact.FileName, out var published)
                    || published.Artifact.Platform != artifact.Platform || published.Artifact.Bytes != artifact.Bytes
                    || published.Artifact.Sha256 != artifact.Sha256 || published.Artifact.Commit != verified.Release.Commit
                    || published.Artifact.Version != verified.Release.Version)
                    throw new InvalidDataException("Signed update metadata does not match the published package catalogue.");
            }
            result.feeds.Add(channel, new(bytes, verified));
        }
        return result;
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
