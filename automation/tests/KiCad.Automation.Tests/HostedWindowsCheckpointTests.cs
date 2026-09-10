using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[DoNotParallelize] // These fixtures replace only the four hosted identity variables, then restore them.
public sealed class HostedWindowsCheckpointTests
{
    private string root = null!;
    private HostedDeliveryRequest request = null!;
    private readonly Dictionary<string, string?> original = new();
    private readonly List<ValidationStep> steps = new();

    [TestInitialize]
    public void Setup()
    {
        root = Directory.CreateTempSubdirectory("kicad-windows-checkpoint-").FullName;
        request = new(root, new string('a', 40), "x64", Path.Combine(root, "output"), new string('b', 40), "prepare");
        foreach (string name in new[] { "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB", "RUNNER_NAME" })
        {
            original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, "synthetic-" + name);
        }
        foreach (string name in new[] { "build/CMakeCache.txt", "build/build.ninja", "vcpkg/vcpkg.exe", "managed/kicad-mcp.exe" })
            Write(name, "Synthetic checkpoint fixture, not native executable evidence: " + name);
        foreach (string name in new[] { "source-commit", "pinned-ancestry", "source-clean", "managed-restore",
            "vcpkg-identity", "vcpkg-bootstrap", "managed-publish", "native-configure", "prepared-source-clean" })
        {
            Write("evidence/" + name + ".out", "Synthetic step output");
            Write("evidence/" + name + ".err", "");
            steps.Add(new(name, 0, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, name + ".out", name + ".err"));
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task PreparationRetainsExactInputsLogsAndOriginalDeadlineWithoutBecomingAPackage()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        await HostedWindowsCheckpoint.WriteAsync(request, deadline, steps, CancellationToken.None);
        var checkpoint = await HostedWindowsCheckpoint.ReadAsync(request with { Phase = "build" }, CancellationToken.None);
        Assert.AreEqual("prepared", checkpoint.Status);
        Assert.AreEqual(deadline, checkpoint.DeadlineAt);
        Assert.AreEqual(steps.Count, checkpoint.Steps.Count);
        Assert.AreEqual(4 + 2 * steps.Count, checkpoint.Files.Count);
        Assert.IsFalse(Directory.Exists(Path.Combine(request.Output, "packages")));
        await Assert.ThrowsExactlyAsync<IOException>(() => HostedWindowsCheckpoint.WriteAsync(request, deadline, steps, CancellationToken.None));
        await HostedWindowsCheckpoint.ClaimAsync(request.Output, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedWindowsCheckpoint.ReadAsync(request, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<IOException>(() => HostedWindowsCheckpoint.ClaimAsync(request.Output, CancellationToken.None));
    }

    [TestMethod]
    public async Task ChangedSourceDependencyTargetAndJobCannotResume()
    {
        var checkpoint = await Prepared();
        foreach (var wrong in new[] { request with { Commit = new string('c', 40) },
            request with { DependencyCommit = new string('c', 40) }, request with { Architecture = "arm64" },
            request with { Repository = Path.Combine(root, "other") }, request with { Output = Path.Combine(root, "other") } })
            Assert.ThrowsExactly<InvalidDataException>(() => checkpoint.RequireMatching(wrong, DateTimeOffset.UtcNow));
        foreach (string name in original.Keys)
        {
            string saved = Environment.GetEnvironmentVariable(name)!;
            Environment.SetEnvironmentVariable(name, "different-job");
            Assert.ThrowsExactly<InvalidDataException>(() => checkpoint.RequireMatching(request, DateTimeOffset.UtcNow));
            Environment.SetEnvironmentVariable(name, saved);
        }
        Assert.ThrowsExactly<InvalidDataException>(() => checkpoint.RequireMatching(request, checkpoint.DeadlineAt));
        Assert.ThrowsExactly<InvalidDataException>(() => (checkpoint with { Status = "failed" }).RequireMatching(request, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    [DataRow("build/CMakeCache.txt")]
    [DataRow("managed/kicad-mcp.exe")]
    [DataRow("evidence/native-configure.err")]
    public async Task ChangedPreparedInputOrLogRejectsWithoutConsumingCheckpoint(string path)
    {
        await Prepared();
        string originalBytes = File.ReadAllText(Path.Combine(request.Output, path));
        Write(path, "changed");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedWindowsCheckpoint.ReadAsync(request, CancellationToken.None));
        Write(path, originalBytes);
        _ = await HostedWindowsCheckpoint.ReadAsync(request, CancellationToken.None);
    }

    [TestMethod]
    public async Task FailureMissingEvidenceAndDuplicateStepsCannotCreatePreparedState()
    {
        var failed = steps.ToArray();
        failed[0] = failed[0] with { ExitCode = 1 };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedWindowsCheckpoint.WriteAsync(request,
            DateTimeOffset.UtcNow.AddMinutes(10), failed, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedWindowsCheckpoint.WriteAsync(request,
            DateTimeOffset.UtcNow.AddMinutes(10), steps.Concat([steps[0]]).ToArray(), CancellationToken.None));
        File.Delete(Path.Combine(request.Output, "evidence/native-configure.err"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HostedWindowsCheckpoint.WriteAsync(request,
            DateTimeOffset.UtcNow.AddMinutes(10), steps, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(request.Output, HostedWindowsCheckpoint.FileName)));
    }

    [TestMethod]
    public async Task CancellationDoesNotClaimTheBuildAndValidContinuationStillWorks()
    {
        await Prepared();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => HostedWindowsCheckpoint.ClaimAsync(request.Output, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => HostedWindowsCheckpoint.ReadAsync(request, cancellation.Token));
        _ = await HostedWindowsCheckpoint.ReadAsync(request, CancellationToken.None);
        await HostedWindowsCheckpoint.ClaimAsync(request.Output, CancellationToken.None);
    }

    [TestMethod]
    public void SplitPhasesAreExplicitAndWindowsOnly()
    {
        foreach (string phase in new[] { "all", "prepare", "build" }) HostedWindowsCheckpoint.RequirePhase(phase, false);
        HostedWindowsCheckpoint.RequirePhase("all", true);
        Assert.ThrowsExactly<ArgumentException>(() => HostedWindowsCheckpoint.RequirePhase("build", true));
        Assert.ThrowsExactly<ArgumentException>(() => HostedWindowsCheckpoint.RequirePhase("prepare", true));
        Assert.ThrowsExactly<ArgumentException>(() => HostedWindowsCheckpoint.RequirePhase("resume-anywhere", false));
    }

    private async Task<HostedWindowsCheckpoint> Prepared()
    {
        await HostedWindowsCheckpoint.WriteAsync(request, DateTimeOffset.UtcNow.AddMinutes(10), steps, CancellationToken.None);
        return await HostedWindowsCheckpoint.ReadAsync(request, CancellationToken.None);
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(request.Output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
