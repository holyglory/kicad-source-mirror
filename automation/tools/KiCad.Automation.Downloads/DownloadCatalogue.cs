using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Downloads;

public sealed record VerifiedDownload(DownloadArtifact Artifact, string Path, DateTime LastWriteUtc);

/// <summary>SA-07: explicit public artifacts only, never general filesystem serving.</summary>
public sealed partial class DownloadCatalogue
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
    public DownloadManifest Manifest { get; }
    public IReadOnlyDictionary<string, VerifiedDownload> Files { get; }

    private DownloadCatalogue(DownloadManifest manifest, Dictionary<string, VerifiedDownload> files)
    { Manifest = manifest; Files = files; }

    public static async Task<DownloadCatalogue> LoadAsync(string root, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("The download directory must be absolute.");
        root = Path.GetFullPath(root);
        string manifestPath = Path.Combine(root, "downloads.json");
        var manifest = JsonSerializer.Deserialize<DownloadManifest>(
            await File.ReadAllBytesAsync(manifestPath, token), JsonOptions)
            ?? throw new InvalidDataException("A download catalogue is required.");
        if (manifest.SchemaVersion != 1 || manifest.Artifacts is null || manifest.Artifacts.Count == 0)
            throw new InvalidDataException("Provide a version 1 catalogue with at least one real artifact.");
        var files = new Dictionary<string, VerifiedDownload>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            token.ThrowIfCancellationRequested();
            if (artifact is null || artifact.FileName is null || !FileNamePattern().IsMatch(artifact.FileName)
                || !CommitPattern().IsMatch(artifact.Commit ?? "")
                || !HashPattern().IsMatch(artifact.SourceSha256 ?? "")
                || !HashPattern().IsMatch(artifact.Sha256 ?? "")
                || artifact.Bytes <= 0 || string.IsNullOrWhiteSpace(artifact.Version)
                || artifact.Platform is not ("linux-x64" or "osx-arm64" or "osx-x64" or "win-x64" or "source"))
                throw new InvalidDataException("An artifact has invalid identity, platform, size or hash.");
            string path = Path.Combine(root, artifact.FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length != artifact.Bytes)
                throw new InvalidDataException("An artifact is absent, linked or has the wrong size: " + artifact.FileName);
            DateTime modified = info.LastWriteTimeUtc;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
            info.Refresh();
            if (digest != artifact.Sha256 || info.Length != artifact.Bytes || info.LastWriteTimeUtc != modified)
                throw new InvalidDataException("An artifact changed or failed its hash: " + artifact.FileName);
            if (!files.TryAdd(artifact.FileName, new(artifact, path, modified)))
                throw new InvalidDataException("Duplicate artifact name: " + artifact.FileName);
        }
        return new(manifest, files);
    }

    public bool TryGetUnchanged(string name, out VerifiedDownload? download)
    {
        if (!Files.TryGetValue(name, out download)) return false;
        var info = new FileInfo(download.Path);
        return info.Exists && info.LinkTarget is null && info.Length == download.Artifact.Bytes
            && info.LastWriteTimeUtc == download.LastWriteUtc;
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\.(?:tar\.gz|zip|dmg|pkg|deb|sha256|sig)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
