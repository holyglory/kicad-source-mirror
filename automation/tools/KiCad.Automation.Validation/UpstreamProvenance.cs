using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Validation;

public sealed record UpstreamProvenance(int SchemaVersion, string Repository, string? Tag, string Commit,
    string Channel, int KaicadRevision, string? FeatureCommit = null)
{
    public const string CanonicalRepository = "https://gitlab.com/kicad/code/kicad.git";
    public const string LegacyPreviewBase = "f638a860a05b3e48d1074314a656ad9b8f597466";
    public const string ManifestPath = "automation/distribution/upstream.json";
    public const string IntegrationPath = "automation/distribution/integration.json";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public void Validate()
    {
        Evidence.RequireCommit(Commit);
        if (FeatureCommit is not null) Evidence.RequireCommit(FeatureCommit);
        if (SchemaVersion != 1 || Repository != CanonicalRepository || KaicadRevision < 1)
            throw new InvalidDataException("Unsupported KAICad upstream provenance.");
        if (Channel == "preview")
        {
            if (Tag is not null) throw new InvalidDataException("Development previews cannot claim a stable upstream tag.");
        }
        else if (Channel is "stable-candidate" or "stable")
        {
            if (Tag is null || !UpstreamMaintenance.IsStable(Tag) || FeatureCommit is null)
                throw new InvalidDataException("Stable candidates require an official version and exact KAICad feature source.");
        }
        else throw new InvalidDataException("Unknown release channel.");
    }

    public static async Task<UpstreamProvenance> ReadAsync(string repository, string commit, CancellationToken token)
    {
        Evidence.RequireCommit(commit);
        string paths = await Git(repository, ["ls-tree", "--name-only", commit, "--", ManifestPath], token);
        UpstreamProvenance provenance;
        if (paths.Trim().Length == 0)
            provenance = new(1, CanonicalRepository, null, LegacyPreviewBase, "preview", 1);
        else
            provenance = JsonSerializer.Deserialize<UpstreamProvenance>(
                await Git(repository, ["show", commit + ":" + ManifestPath], token), Json)
                ?? throw new InvalidDataException("Missing upstream provenance.");
        provenance.Validate();
        await Git(repository, ["merge-base", "--is-ancestor", provenance.Commit, commit], token);
        return provenance;
    }

    public static async Task<UpstreamProvenance> RequireAsync(string repository, string commit,
        CancellationToken token)
    {
        var provenance = await ReadAsync(repository, commit, token);
        if (provenance.Tag is not null)
        {
            var integration = JsonSerializer.Deserialize<UpstreamIntegration>(await Git(repository,
                ["show", commit + ":" + IntegrationPath], token), Json);
            if (integration is null || integration.SchemaVersion != 1 || integration.Status != "integrated"
                || integration.UpstreamTag != provenance.Tag || integration.UpstreamCommit != provenance.Commit
                || integration.FeatureCommit != provenance.FeatureCommit)
                throw new InvalidDataException("This candidate still needs integration repair.");
            await Git(repository, ["cat-file", "-e", provenance.FeatureCommit + "^{commit}"], token);
            var tags = UpstreamMaintenance.ParseTags(await Git(repository,
                ["ls-remote", CanonicalRepository, "refs/tags/" + provenance.Tag, "refs/tags/" + provenance.Tag + "^{}"], token));
            if (!tags.TryGetValue(provenance.Tag, out string? actual) || actual != provenance.Commit)
                throw new InvalidDataException("The official upstream tag no longer matches this candidate.");
        }
        return provenance;
    }

    internal static Task<string> Git(string repository, string[] args, CancellationToken token)
        => LinuxPackage.RunAsync("git", args, repository, token);
}
