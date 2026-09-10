using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;
using static KiCad.Automation.Validation.HostedPreviewStaging;

namespace KiCad.Automation.Validation;

public sealed record SignedLinuxPreviewRequest(string Candidate, string Commit, string Previous,
    string Output, string Feed, string Publisher);
public sealed record SignedLinuxPreviewResult(string Status, string Directory, string Commit,
    long Sequence, int ArtifactCount, string CatalogueSha256, bool QualifyingDelivery = false);

/// <summary>Stage an authenticated Linux preview without publishing it or changing an installation.</summary>
public static class SignedLinuxPreviewStaging
{
    public static async Task<SignedLinuxPreviewResult> RunAsync(SignedLinuxPreviewRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Evidence.RequireCommit(request.Commit);
        string candidate = AbsoluteDirectory(request.Candidate), previous = AbsoluteDirectory(request.Previous);
        if (new[] { request.Output, request.Feed, request.Publisher }.Any(x => !Path.IsPathFullyQualified(x)))
            throw new ArgumentException("Use absolute output, signed feed and trusted publisher paths.");
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Output));
        if (Directory.Exists(output) || File.Exists(output) || Inside(output, candidate) || Inside(output, previous))
            throw new ArgumentException("Output must be new and outside both input trees.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var catalogue = JsonSerializer.Deserialize<DownloadManifest>(await Metadata(Path.Combine(candidate, "downloads.json"), token), options)
            ?? throw new InvalidDataException("Missing Linux candidate catalogue.");
        if (catalogue.SchemaVersion != 1 || catalogue.Artifacts is null || catalogue.Artifacts.Count != 2
            || catalogue.Artifacts.Any(x => x is null))
            throw new InvalidDataException("Require one Linux archive and its matching source archive.");
        var apps = catalogue.Artifacts.Where(x => x.Platform == "linux-x64").ToArray();
        var sources = catalogue.Artifacts.Where(x => x.Platform == "source").ToArray();
        if (apps.Length != 1 || sources.Length != 1) throw new InvalidDataException("Ambiguous Linux/source package identities.");
        var app = apps[0]; var source = sources[0];
        if (app.FileName == source.FileName || app.Sha256 == source.Sha256
            || catalogue.Artifacts.Any(x => x.Commit != request.Commit || x.Version != app.Version
                || x.SourceSha256 != source.Sha256 || x.FileName is null || !x.FileName.EndsWith(".tar.gz", StringComparison.Ordinal)))
            throw new InvalidDataException("The Linux candidate source, version or archive format is inconsistent.");

        byte[] publisher = await Metadata(request.Publisher, token);
        byte[] envelope = await Metadata(request.Feed, token);
        byte[] previousEnvelope = await Metadata(Path.Combine(previous, "updates", "preview.json"), token);
        var old = UpdateManifestCodec.Verify(previousEnvelope, publisher, "preview");
        var next = UpdateManifestCodec.Verify(envelope, publisher, "preview", new(old.Release.Sequence, old.PayloadSha256));
        if (next.Release.Sequence <= old.Release.Sequence || next.Release.Commit != request.Commit
            || next.Release.Version != app.Version || next.Release.Artifacts.Count != 1)
            throw new InvalidDataException("Require a strictly newer same-candidate Linux preview feed.");
        // Older installed Linux clients cannot parse the expanded Mac/Windows
        // metadata. Keep this bootstrap release Linux-only; other downloads stay.
        var artifact = next.ForInstallation("linux-x64", "tar.gz");
        if (artifact is null || artifact.FileName != app.FileName || artifact.Bytes != app.Bytes || artifact.Sha256 != app.Sha256)
            throw new InvalidDataException("The signed preview feed does not identify this Linux archive.");
        var incoming = catalogue.Artifacts.Select(x => new PublicPreviewArtifact(x, Path.Combine(candidate, x.FileName))).ToArray();
        var feed = new PublicPreviewFeed("preview", envelope, Convert.ToHexStringLower(SHA256.HashData(previousEnvelope)));
        var staged = await StagePublicAsync(previous, output, incoming, feed, token);
        return new("staged", output, request.Commit, next.Release.Sequence, staged.Count, staged.Hash);
    }
}
