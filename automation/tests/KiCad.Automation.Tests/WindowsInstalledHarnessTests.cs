using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsInstalledHarnessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DisconnectDoesNotWaitForOrKillAnInheritedDiagnosticWriter()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows inherited pipe handles and native process jobs."); return; }
        string scratch = Directory.CreateTempSubdirectory("kwmcppipe-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-mcp-diagnostic-drain")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var job = new WindowsProcessJob(); Process? child = null;
        try
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/windows-mcp-pipe-fixture.cpp"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string executable = Path.Combine(scratch, "mcp-pipe.exe");
            var compile = await WindowsLauncherTests.Invoke("cl.exe", ["/nologo", "/EHsc", "/std:c++20", "/MT", "/utf-8",
                "/I" + Path.Combine(repository.FullName, "thirdparty/nlohmann_json"),
                Path.Combine(repository.FullName, "automation/tests/fixtures/windows-mcp-pipe-fixture.cpp"), "/Fe:" + executable], scratch, deadline.Token, input: null);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stdout.log"), compile.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stderr.log"), compile.Error, deadline.Token);
            Assert.AreEqual(0, compile.ExitCode, compile.Output + compile.Error);
            var client = await WindowsInstalledPackageTests.Mcp.Start(executable, scratch, Path.Combine(scratch, "state"), evidence,
                "inherited", job, deadline.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "pipe-child.pid"), deadline.Token), CultureInfo.InvariantCulture));
            Assert.IsTrue(job.Contains(child)); Assert.IsFalse(child.HasExited);
            var timer = Stopwatch.StartNew();
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsFalse(child.HasExited, "MCP disconnect must not kill a still-running native child just to close diagnostics.");
            using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "inherited-mcp-capture.json"), deadline.Token));
            Assert.IsFalse(capture.RootElement.GetProperty("reachedEof").GetBoolean(), "The fixture still owns a writer; never label truncated diagnostics as complete.");
            string receipt = Path.Combine(evidence, "result.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { schemaVersion = 1, status = "passed",
                syntheticProtocol = true, nativeInheritedPipe = true, disconnectMilliseconds = timer.ElapsedMilliseconds,
                childPreserved = true, originalKiCadJourneyVerified = false }), deadline.Token);
            TestContext.AddResultFile(receipt);
        }
        finally
        {
            job.Dispose();
            if (child is not null) { await child.WaitForExitAsync(); child.Dispose(); }
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
        }
    }
}
