using System.Text.Json;

namespace KiCad.Automation.Validation;

/// <summary>A continuation within one disposable job, not a reusable build cache or native qualification.</summary>
public sealed record HostedWindowsCheckpoint(int SchemaVersion, string Status, string SourceCommit,
    string DependencyCommit, string Architecture, string Repository, string Output, string RunId,
    string RunAttempt, string Job, string Runner, DateTimeOffset DeadlineAt,
    IReadOnlyList<ValidationStep> Steps, IReadOnlyList<EvidenceFile> Files)
{
    public const string FileName = "windows-prepared.json";
    private const string ClaimName = "windows-build-started.json";

    public static void RequirePhase(string phase, bool mac)
    {
        if (phase is not ("all" or "prepare" or "build") || (mac && phase != "all"))
            throw new ArgumentException("Use all, or Windows-only prepare followed by build in the same job.");
    }

    public static async Task WriteAsync(HostedDeliveryRequest request, DateTimeOffset deadlineAt,
        IReadOnlyList<ValidationStep> steps, CancellationToken token)
    {
        string output = Path.GetFullPath(request.Output);
        string[] fixedFiles = ["build/CMakeCache.txt", "build/build.ninja", "vcpkg/vcpkg.exe"];
        string[] files = fixedFiles.Select(x => Path.Combine(output, x))
            .Concat(Directory.GetFiles(Path.Combine(output, "managed"), "*", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Combine(output, "evidence"), "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).ToArray();
        if (!File.Exists(Path.Combine(output, "managed", "kicad-mcp.exe")))
            throw new InvalidDataException("The prepared managed runtime is missing.");
        var inventory = new List<EvidenceFile>();
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            inventory.Add(new(Path.GetRelativePath(output, file).Replace('\\', '/'), new FileInfo(file).Length, Evidence.Hash(file)));
        }
        var checkpoint = new HostedWindowsCheckpoint(1, "prepared", request.Commit, request.DependencyCommit,
            request.Architecture, Path.GetFullPath(request.Repository), output,
            Identity("GITHUB_RUN_ID"), Identity("GITHUB_RUN_ATTEMPT"), Identity("GITHUB_JOB"), Identity("RUNNER_NAME"),
            deadlineAt, steps.ToArray(), inventory);
        checkpoint.RequireMatching(request, DateTimeOffset.UtcNow);
        await using var stream = new FileStream(Path.Combine(output, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, checkpoint, Evidence.JsonOptions, token);
    }

    public static async Task<HostedWindowsCheckpoint> ReadAsync(HostedDeliveryRequest request, CancellationToken token)
    {
        string output = Path.GetFullPath(request.Output);
        // A failed/interrupted final stage needs a fresh job, not a second install
        // over partly populated packaging directories.
        if (File.Exists(Path.Combine(output, ClaimName)))
            throw new InvalidDataException("This prepared build was already claimed; preserve its evidence and use a fresh job.");
        await using var stream = File.OpenRead(Path.Combine(output, FileName));
        var checkpoint = await JsonSerializer.DeserializeAsync<HostedWindowsCheckpoint>(stream, Evidence.JsonOptions, token)
            ?? throw new InvalidDataException("Missing Windows preparation checkpoint.");
        checkpoint.RequireMatching(request, DateTimeOffset.UtcNow);
        foreach (var file in checkpoint.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(output, file.Path));
            if (!path.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path) || new FileInfo(path).Length != file.Bytes || Evidence.Hash(path) != file.Sha256)
                throw new InvalidDataException("Prepared input or evidence changed: " + file.Path);
        }
        return checkpoint;
    }

    public void RequireMatching(HostedDeliveryRequest request, DateTimeOffset now)
    {
        if (SchemaVersion != 1 || Status != "prepared" || SourceCommit != request.Commit
            || DependencyCommit != request.DependencyCommit || Architecture != "x64" || Architecture != request.Architecture
            || Repository != Path.GetFullPath(request.Repository) || Output != Path.GetFullPath(request.Output)
            || RunId != Identity("GITHUB_RUN_ID") || RunAttempt != Identity("GITHUB_RUN_ATTEMPT")
            || Job != Identity("GITHUB_JOB") || Runner != Identity("RUNNER_NAME")
            || DeadlineAt <= now || DeadlineAt > now.AddMinutes(330)
            || Steps is null || Steps.Count == 0 || Steps.Any(x => x is null || x.ExitCode != 0)
            || Steps.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != Steps.Count
            || Files is null || Files.Count == 0 || Files.Any(x => x is null)
            || Files.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != Files.Count)
            throw new InvalidDataException("Windows preparation does not match this exact source, dependency, job, attempt and output, or is incomplete/expired.");
        foreach (string name in new[] { "source-commit", "pinned-ancestry", "source-clean", "managed-restore",
            "vcpkg-identity", "vcpkg-bootstrap", "managed-publish", "native-configure", "prepared-source-clean" })
            if (!Steps.Any(x => x.Name == name)) throw new InvalidDataException("Missing successful preparation step: " + name);
        foreach (string name in new[] { "build/CMakeCache.txt", "build/build.ninja", "vcpkg/vcpkg.exe", "managed/kicad-mcp.exe" })
            if (!Files.Any(x => x.Path == name)) throw new InvalidDataException("Missing prepared input: " + name);
        foreach (var step in Steps)
            if (!Files.Any(x => x.Path == "evidence/" + step.StandardOutput)
                || !Files.Any(x => x.Path == "evidence/" + step.StandardError))
                throw new InvalidDataException("Missing preparation logs: " + step.Name);
    }

    public static async Task ClaimAsync(string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(Path.Combine(output, ClaimName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, new { Status = "build_started", StartedAt = DateTimeOffset.UtcNow }, Evidence.JsonOptions, token);
    }

    private static string Identity(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidDataException("Missing hosted job identity: " + name);
}
