using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsProcessIdentityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void IdentitySerializationPreservesTheExactKernelCounter()
    {
        var identity = new WindowsProcessIdentity(123, "134200000000000001", Path.GetFullPath("fixture.exe"));
        identity.Validate();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.AreEqual(JsonValueKind.String, json.RootElement.GetProperty("creationFileTime").ValueKind);
        Assert.AreEqual(identity, json.RootElement.Deserialize<WindowsProcessIdentity>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        foreach (string invalid in new[] { "0", "0123", "-1", "1e12", "", "18446744073709551616" })
            Assert.ThrowsExactly<InvalidDataException>(() => (identity with { CreationFileTime = invalid }).Validate());
        Assert.ThrowsExactly<InvalidDataException>(() => (identity with { ProcessId = 0 }).Validate());
        Assert.ThrowsExactly<InvalidDataException>(() => (identity with { Executable = "relative.exe" }).Validate());
        if (!OperatingSystem.IsWindows()) Assert.ThrowsExactly<PlatformNotSupportedException>(() => WindowsProcessIdentity.Read(Environment.ProcessId));
    }

    [TestMethod]
    public async Task NativeIdentityAndPinnedWaitRejectWrongTargetsAndPreserveACancelledProcess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires real Windows kernel process handles."); return; }
        string root = Directory.CreateTempSubdirectory("kwidentity-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!, "windows-process-identity")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? child = null;
        try
        {
            string executable = await Compile(root, evidence, deadline.Token);
            var managed = WindowsProcessIdentity.Read(Environment.ProcessId);
            var probe = await WindowsLauncherTests.Invoke(executable, [Environment.ProcessId.ToString()], root, deadline.Token, input: null);
            Assert.AreEqual(0, probe.ExitCode);
            var native = JsonSerializer.Deserialize<WindowsProcessIdentity>(probe.Output, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.AreEqual(managed, native, "Native headers and managed bindings must report the same unrounded identity.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "identity.json"), probe.Output, deadline.Token);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("wait"); child = Process.Start(start)!;
            Assert.AreEqual("ready", await child.StandardOutput.ReadLineAsync(deadline.Token));
            var expected = WindowsProcessIdentity.Read(child.Id);
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsObservedProcess.Open(expected with { CreationFileTime = "1" }));
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsObservedProcess.Open(expected with { Executable = managed.Executable }));
            using var observed = WindowsObservedProcess.Open(expected);
            Assert.IsTrue(observed.IsAlive);
            using (var cancelled = new CancellationTokenSource(100))
                await Assert.ThrowsAsync<OperationCanceledException>(() => observed.WaitForExitAsync(cancelled.Token));
            Assert.IsTrue(observed.IsAlive); Assert.IsFalse(child.HasExited);
            Task exit = observed.WaitForExitAsync(deadline.Token);
            observed.Dispose(); // The wait retains this kernel object independently.
            await child.StandardInput.WriteLineAsync("exit"); child.StandardInput.Close();
            await exit; await child.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, child.ExitCode);
            try
            {
                using var unexpected = WindowsObservedProcess.Open(expected);
                Assert.Fail("An exited process must not be accepted as the old editor.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { }
            string receipt = Path.Combine(evidence, "result.json");
            await File.WriteAllTextAsync(receipt, "{\"schemaVersion\":1,\"status\":\"passed\",\"nativeKernelIdentityVerified\":true,\"cancellationPreservedProcess\":true,\"pinnedHandleObservedExit\":true,\"nativeEditorRestarted\":false}", deadline.Token);
            TestContext.AddResultFile(receipt); TestContext.AddResultFile(Path.Combine(evidence, "identity.json"));
        }
        finally
        {
            if (child is not null) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); child.Dispose(); }
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
        }
    }

    internal static async Task<string> Compile(string root, string evidence, CancellationToken token)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/windows-process-probe.cpp"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string executable = Path.Combine(root, "identity.exe");
        var result = await WindowsLauncherTests.Invoke("cl.exe", ["/nologo", "/EHsc", "/std:c++17", "/MT",
            "/I" + Path.Combine(repository.FullName, "thirdparty/nlohmann_json"),
            Path.Combine(repository.FullName, "automation/tests/fixtures/windows-process-probe.cpp"), "/Fe:" + executable], root, token, input: null);
        await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stdout.log"), result.Output, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stderr.log"), result.Error, token);
        Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
        return executable;
    }
}
