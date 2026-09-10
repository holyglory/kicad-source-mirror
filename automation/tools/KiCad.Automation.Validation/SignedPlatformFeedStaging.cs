using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;
using static KiCad.Automation.Validation.HostedPreviewStaging;

namespace KiCad.Automation.Validation;

public sealed record PlatformFeedSource(string Platform, string Channel, string EnvelopePath);
public sealed record PlatformFeedSources(int SchemaVersion, IReadOnlyList<PlatformFeedSource> Feeds);
public sealed record PlatformFeedStageRequest(string Previous, string Output, string Publisher,
    IReadOnlyList<PlatformFeedSource> Feeds);
public sealed record PlatformFeedStageResult(string Status, string Directory, int ChangedFeeds, int ArtifactCount,
    string CatalogueSha256, bool QualifyingDelivery = false);

public static class SignedPlatformFeedStaging
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public static async Task<PlatformFeedSources> ReadSourcesAsync(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute feed-source declaration path.");
        var sources = JsonSerializer.Deserialize<PlatformFeedSources>(await Metadata(path, token), Json);
        if (sources is null || sources.SchemaVersion != 1) throw new InvalidDataException("Unsupported platform-feed declaration.");
        return sources;
    }

    public static async Task<PlatformFeedStageResult> RunAsync(PlatformFeedStageRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string previous = AbsoluteDirectory(request.Previous);
        if (!Path.IsPathFullyQualified(request.Output) || !Path.IsPathFullyQualified(request.Publisher))
            throw new ArgumentException("Use absolute publication and trusted publisher paths.");
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Output));
        if (Directory.Exists(output) || File.Exists(output) || Inside(output, previous))
            throw new ArgumentException("Publication output must be new and outside the current download tree.");
        if (request.Feeds is null || request.Feeds.Count is < 1 or > 6 || request.Feeds.Any(x => x is null))
            throw new ArgumentException("Select one to six explicit native platform/channel feeds.");
        byte[] key = await Metadata(request.Publisher, token);
        _ = UpdateManifestCodec.ValidatePublisherKey(key);
        byte[] catalogueBytes = await Metadata(Path.Combine(previous, "downloads.json"), token);
        var catalogue = JsonSerializer.Deserialize<DownloadManifest>(catalogueBytes, Json)
            ?? throw new InvalidDataException("Missing existing public catalogue.");
        if (catalogue.SchemaVersion != 1 || catalogue.Artifacts is null || catalogue.Artifacts.Any(x => x is null))
            throw new InvalidDataException("Invalid existing public catalogue.");
        var files = catalogue.Artifacts.ToDictionary(x => x.FileName, StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var replacements = new List<PublicPreviewFeed>();
        foreach (var source in request.Feeds)
        {
            string relative = PlatformUpdateFeeds.RelativeFeed(source.Platform, source.Channel);
            if (!paths.Add(relative) || !Path.IsPathFullyQualified(source.EnvelopePath))
                throw new ArgumentException("Use unique platform/channel targets and absolute envelope paths.");
            byte[] bytes = await Metadata(source.EnvelopePath, token);
            var next = UpdateManifestCodec.Verify(bytes, key, source.Channel);
            foreach (var artifact in next.Release.Artifacts)
                if (artifact.Platform != source.Platform || !files.TryGetValue(artifact.FileName, out var published)
                    || published.Platform != source.Platform || published.Version != next.Release.Version
                    || published.Commit != next.Release.Commit || published.Bytes != artifact.Bytes || published.Sha256 != artifact.Sha256)
                    throw new InvalidDataException("Native feed does not identify exact already-published target bytes.");
            string oldPath = Path.Combine(previous, relative);
            string? previousHash = null;
            if (File.Exists(oldPath))
            {
                byte[] oldBytes = await Metadata(oldPath, token);
                var old = UpdateManifestCodec.Verify(oldBytes, key, source.Channel);
                _ = UpdateManifestCodec.Verify(bytes, key, source.Channel, new(old.Release.Sequence, old.PayloadSha256));
                if (next.PayloadSha256 == old.PayloadSha256) continue; // Preserve the old signature bytes; no churn.
                if (next.Release.Sequence <= old.Release.Sequence) throw new InvalidDataException("Native update sequence did not advance.");
                previousHash = Convert.ToHexStringLower(SHA256.HashData(oldBytes));
            }
            replacements.Add(new(relative, bytes, previousHash));
        }
        if (replacements.Count == 0)
        {
            foreach (var artifact in catalogue.Artifacts)
            {
                if (Path.GetFileName(artifact.FileName) != artifact.FileName || artifact.FileName.Contains('\\'))
                    throw new InvalidDataException("Invalid existing public artifact path.");
                await VerifyFile(Path.Combine(previous, artifact.FileName), artifact.Bytes, artifact.Sha256, token);
            }
            return new("unchanged", previous, 0, catalogue.Artifacts.Count, Convert.ToHexStringLower(SHA256.HashData(catalogueBytes)));
        }
        var result = await StagePublicAsync(previous, output, [], replacements, token);
        return new("staged", output, replacements.Count, result.Count, result.Hash);
    }
}
