using System.Text.Json;
using System.Text.RegularExpressions;

namespace KiCad.Automation.Validation;

public sealed record UpstreamTracking(int SchemaVersion, string Repository, Dictionary<string, string> InitialTags);
public sealed record UpstreamTarget(string Tag, string Commit);
public sealed record UpstreamDetection(int SchemaVersion, UpstreamTarget[] Targets, int ExistingCandidates);
public sealed record UpstreamIntegration(int SchemaVersion, string Status, string UpstreamTag,
    string UpstreamCommit, string FeatureCommit, string FeatureBase, string[] Conflicts);
public sealed record UpstreamCandidate(int SchemaVersion, string Status, string Tag, string UpstreamCommit,
    string FeatureCommit, string CandidateCommit, string CandidateBranch, string MaintenanceBranch,
    string Directory, bool CreateMaintenanceBranch, string[] Conflicts);

/// <summary>Finite Git operations. Scheduling, native execution and publication remain owned by their existing services.</summary>
public static partial class UpstreamMaintenance
{
    public const string TrackingPath = "automation/distribution/upstream-tracking.json";
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?$")]
    private static partial Regex StablePattern();
    public static bool IsStable(string tag) => StablePattern().IsMatch(tag)
        && Version.TryParse(tag, out var version) && version.Minor != 99;

    public static Dictionary<string, string> ParseTags(string output)
    {
        var direct = new Dictionary<string, string>(StringComparer.Ordinal);
        var peeled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 2 || !columns[1].StartsWith("refs/tags/", StringComparison.Ordinal))
                throw new InvalidDataException("Malformed upstream tag response.");
            string tag = columns[1][10..];
            bool peel = tag.EndsWith("^{}", StringComparison.Ordinal);
            if (peel) tag = tag[..^3];
            if (!IsStable(tag)) continue;
            Evidence.RequireCommit(columns[0]);
            var target = peel ? peeled : direct;
            if (target.TryGetValue(tag, out var old) && old != columns[0])
                throw new InvalidDataException("Conflicting upstream tag records: " + tag);
            target[tag] = columns[0];
        }
        foreach (var pair in peeled) direct[pair.Key] = pair.Value;
        if (direct.Count == 0) throw new InvalidDataException("No stable upstream tags were returned; do not advance tracking.");
        return direct;
    }

    public static UpstreamDetection Detect(UpstreamTracking tracking, IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, string> candidates)
    {
        if (tracking.SchemaVersion != 1 || tracking.Repository != UpstreamProvenance.CanonicalRepository
            || tracking.InitialTags.Count == 0 || tags.Count == 0)
            throw new InvalidDataException("Missing initialized upstream tracking.");
        foreach (var known in tracking.InitialTags.Concat(candidates))
        {
            Evidence.RequireCommit(known.Value);
            if (!IsStable(known.Key) || !tags.TryGetValue(known.Key, out var sha) || sha != known.Value)
                throw new InvalidDataException("Previously observed upstream tag changed or disappeared: " + known.Key);
        }
        var targets = tags.Where(t => !tracking.InitialTags.ContainsKey(t.Key) && !candidates.ContainsKey(t.Key))
            .OrderBy(t => Version.Parse(t.Key)).Select(t => new UpstreamTarget(t.Key, t.Value)).ToArray();
        return new(1, targets, candidates.Count);
    }

    public static async Task<UpstreamDetection> DetectAsync(string repository, CancellationToken token)
    {
        var tracking = JsonSerializer.Deserialize<UpstreamTracking>(await File.ReadAllTextAsync(
            Path.Combine(repository, TrackingPath), token), UpstreamProvenance.Json)
            ?? throw new InvalidDataException("Missing upstream tracking.");
        var tags = ParseTags(await Git(repository,
            ["ls-remote", UpstreamProvenance.CanonicalRepository, "refs/tags/[0-9]*"], token));
        string remote = await Git(repository, ["ls-remote", "origin", "refs/heads/codex/upstream/*"], token);
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in remote.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 2) throw new InvalidDataException("Malformed candidate ref response.");
            string tag = columns[1].Split('/').Last();
            if (!IsStable(tag)) continue;
            Evidence.RequireCommit(columns[0]);
            await Git(repository, ["fetch", "--no-tags", "origin", columns[1]], token);
            string fetched = (await Git(repository, ["rev-parse", "FETCH_HEAD"], token)).Trim();
            if (fetched != columns[0]) throw new InvalidDataException("Candidate moved during inspection; retry discovery.");
            var provenance = await UpstreamProvenance.ReadAsync(repository, fetched, token);
            if (provenance.Tag != tag) throw new InvalidDataException("Candidate branch has mismatched upstream provenance.");
            candidates.Add(tag, provenance.Commit);
        }
        return Detect(tracking, tags, candidates);
    }

    public static async Task<UpstreamCandidate> PrepareAsync(string repository, string tag, string upstreamCommit,
        string output, CancellationToken token, string upstreamRemote = UpstreamProvenance.CanonicalRepository)
    {
        if (!IsStable(tag)) throw new ArgumentException("Select an exact stable KiCad version.");
        Evidence.RequireCommit(upstreamCommit);
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Candidate output must be a new absolute directory.");
        string feature = (await Git(repository, ["rev-parse", "HEAD"], token)).Trim();
        await LinuxPackage.RequireSourceAsync(repository, feature, token);
        var source = await UpstreamProvenance.ReadAsync(repository, feature, token);
        await Git(repository, ["fetch", "--no-tags", upstreamRemote, "refs/tags/" + tag], token);
        if ((await Git(repository, ["rev-parse", "FETCH_HEAD^{commit}"], token)).Trim() != upstreamCommit)
            throw new InvalidDataException("Upstream changed between detection and integration.");
        var version = Version.Parse(tag);
        string maintenance = $"release/{version.Major}.{version.Minor}";
        string candidateBranch = "codex/upstream/" + tag;
        string remoteRefs = await Git(repository, ["ls-remote", "origin", "refs/heads/" + candidateBranch,
            "refs/heads/" + maintenance], token);
        if (remoteRefs.Contains("refs/heads/" + candidateBranch + "\n", StringComparison.Ordinal))
            throw new InvalidDataException("The candidate already exists. Repair it in place; never replace its history.");
        bool createMaintenance = !remoteRefs.Contains("refs/heads/" + maintenance, StringComparison.Ordinal);
        string? maintained = null;
        string? previousFeatures = null;
        if (!createMaintenance)
        {
            await Git(repository, ["fetch", "--no-tags", "origin", "refs/heads/" + maintenance], token);
            string head = (await Git(repository, ["rev-parse", "FETCH_HEAD"], token)).Trim();
            string manifest = await Git(repository, ["ls-tree", "--name-only", head, "--", UpstreamProvenance.ManifestPath], token);
            if (manifest.Trim().Length > 0)
            {
                var previous = await UpstreamProvenance.ReadAsync(repository, head, token);
                if (previous.Tag is null || Version.Parse(previous.Tag) >= version)
                    throw new InvalidDataException("The maintenance branch is not an older stable release.");
                maintained = head;
                previousFeatures = previous.FeatureCommit;
            }
        }
        Directory.CreateDirectory(output);
        string checkout = Path.Combine(output, "candidate");
        string baseline = maintained ?? upstreamCommit;
        await Git(repository, ["worktree", "add", "--detach", checkout, baseline], token);
        string[] conflicts = [];
        string status = "integrated";
        try
        {
            if (maintained is null)
            {
                string patch = await Git(repository, ["diff", "--binary", "--full-index", source.Commit, feature, "--", ".",
                    ":(exclude)" + UpstreamProvenance.ManifestPath, ":(exclude)" + UpstreamProvenance.IntegrationPath], token);
                string patchFile = Path.Combine(output, "kaicad.patch");
                await File.WriteAllTextAsync(patchFile, patch, token);
                if (patch.Length > 0) await Git(checkout, ["apply", "--3way", "--index", patchFile], token);
            }
            else
            {
                await Git(checkout, ["-c", "user.name=KAICad automation", "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                    "merge", "--no-ff", "--no-commit", upstreamCommit], token);
                if (previousFeatures is not null && previousFeatures != feature)
                {
                    string patch = await Git(repository, ["diff", "--binary", "--full-index", previousFeatures, feature, "--", ".",
                        ":(exclude)" + UpstreamProvenance.ManifestPath, ":(exclude)" + UpstreamProvenance.IntegrationPath], token);
                    string patchFile = Path.Combine(output, "kaicad.patch");
                    await File.WriteAllTextAsync(patchFile, patch, token);
                    if (patch.Length > 0) await Git(checkout, ["apply", "--3way", "--index", patchFile], token);
                }
            }
        }
        catch (InvalidDataException error)
        {
            status = "conflicts";
            conflicts = (await Git(checkout, ["diff", "--name-only", "--diff-filter=U"], token))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await File.WriteAllTextAsync(Path.Combine(output, "integration-error.txt"), error.Message, token);
            // This checkout was created above solely for this attempt. Preserve the
            // diagnostic patch and keep the source and all existing candidate branches unchanged.
            await Git(checkout, ["reset", "--hard", baseline], token);
        }
        var provenance = new UpstreamProvenance(1, UpstreamProvenance.CanonicalRepository, tag,
            upstreamCommit, "stable-candidate", 1, feature);
        // A failed merge must still include the new upstream commit as ancestry,
        // but never pretend the old code was integrated. Build admission checks the status.
        if (status == "conflicts" && maintained is not null)
            await Git(checkout, ["reset", "--hard", upstreamCommit], token);
        Directory.CreateDirectory(Path.Combine(checkout, "automation/distribution"));
        await File.WriteAllTextAsync(Path.Combine(checkout, UpstreamProvenance.ManifestPath),
            JsonSerializer.Serialize(provenance, UpstreamProvenance.Json) + "\n", token);
        await File.WriteAllTextAsync(Path.Combine(checkout, UpstreamProvenance.IntegrationPath),
            JsonSerializer.Serialize(new UpstreamIntegration(1, status, tag, upstreamCommit, feature, source.Commit, conflicts),
                UpstreamProvenance.Json) + "\n", token);
        await Git(checkout, ["add", "--all"], token);
        await Git(checkout, ["-c", "user.name=KAICad automation", "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
            "commit", "-m", $"Prepare KiCad {tag} integration ({status})"], token);
        string candidate = (await Git(checkout, ["rev-parse", "HEAD"], token)).Trim();
        var result = new UpstreamCandidate(1, status, tag, upstreamCommit, feature, candidate,
            candidateBranch, maintenance, checkout, createMaintenance, conflicts);
        await File.WriteAllTextAsync(Path.Combine(output, "candidate.json"),
            JsonSerializer.Serialize(result, UpstreamProvenance.Json) + "\n", token);
        string review = $"# KAICad on KiCad {tag}\n\n"
            + $"Integration: **{status}**. Upstream commit: `{upstreamCommit}`.\n\n"
            + $"KAICad feature source: `{feature}`. Candidate commit: `{candidate}`.\n\n"
            + (status == "integrated"
                ? "Managed checks and native builds follow in the workflow. Linux and complete native application/MCP, design-preservation and signed-update qualification are required before owner review.\n\n"
                : "Porting repair is required. This branch does not contain a completed KAICad port. Inspect the retained patch and integration-error artifact; preserve all KAICad functionality before changing the integration result.\n\n")
            + "No stable release, signing, feed update or automatic merge is performed. The owner must approve the exact verified candidate before publication; follow RELEASES.md on main.\n";
        await File.WriteAllTextAsync(Path.Combine(output, "review.md"), review, token);
        return result;
    }

    private static Task<string> Git(string repository, string[] args, CancellationToken token)
        => UpstreamProvenance.Git(repository, args, token);
}
