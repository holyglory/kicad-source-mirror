namespace KiCad.Automation.Distribution;

// Public release metadata, deliberately separate from private engineering data
// and from any future authenticated update instructions.
public sealed record DownloadArtifact(string FileName, string Platform, string Version,
    string Commit, string SourceSha256, long Bytes, string Sha256);
public sealed record DownloadManifest(int SchemaVersion, IReadOnlyList<DownloadArtifact> Artifacts);
