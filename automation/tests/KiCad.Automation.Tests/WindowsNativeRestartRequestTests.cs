using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsNativeRestartRequestTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeClickSnapshotsCurrentSelectionAndExactIdentityWithoutChangingTheStore()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the production Windows C++ request builder."); return; }
        string root = Directory.CreateTempSubdirectory("kwrequest-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-native-restart-request")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "kicad/automation_update_windows.cpp"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string executable = Path.Combine(root, "request.exe");
            var compiled = await WindowsLauncherTests.Invoke("cl.exe", ["/nologo", "/EHsc", "/std:c++20", "/MT", "/utf-8",
                "/I" + Path.Combine(repository.FullName, "thirdparty/nlohmann_json"), "/I" + Path.Combine(repository.FullName, "kicad"),
                Path.Combine(repository.FullName, "kicad/automation_update_windows.cpp"),
                Path.Combine(repository.FullName, "automation/tests/fixtures/windows-restart-request.cpp"), "/Fe:" + executable], root, deadline.Token, input: null);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stdout.log"), compiled.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stderr.log"), compiled.Error, deadline.Token);
            Assert.AreEqual(0, compiled.ExitCode, compiled.Output + compiled.Error);

            string installation = Directory.CreateDirectory(Path.Combine(root, "安装 Δ")).FullName;
            string manager = Directory.CreateDirectory(Path.Combine(installation, "manager")).FullName;
            string selectionPath = Path.Combine(manager, "current.json");
            string project = Path.Combine(installation, "µ controller.kicad_pro");
            await File.WriteAllTextAsync(project, "{}", deadline.Token);
            string[] files = [selectionPath, project];
            Guid instance = Guid.NewGuid();
            string? priorSocket = null;
            foreach (string projectPath in new[] { project, "" })
            {
                // A new click must read a selection changed by another editor,
                // not the selection remembered at background download time.
                string selection = Guid.NewGuid().ToString("D");
                string record = JsonSerializer.Serialize(new { schemaVersion = 1, selectionId = selection,
                    versionTarget = "versions/" + new string('b', 64) + "/payload" });
                await File.WriteAllTextAsync(selectionPath, record, deadline.Token);
                Guid operation = Guid.NewGuid();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
                foreach (string arg in new[] { installation, projectPath, operation.ToString("D"), instance.ToString("D"), "true" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                try
                {
                    string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                    if (line is null) Assert.Fail(await process.StandardError.ReadToEndAsync(deadline.Token));
                    Assert.IsNotNull(line);
                    var request = WindowsRestartCommand.Parse(Encoding.UTF8.GetBytes(line)).Request;
                    WindowsUpdateHandoff.Validate(request);
                    var expectedIdentity = WindowsProcessIdentity.Read(process.Id);
                    Assert.AreEqual(expectedIdentity, request.OldProcess);
                    Assert.AreEqual(selection, request.ExpectedSelectionId);
                    Assert.AreEqual(operation, request.OperationId); Assert.AreEqual(instance, request.InstanceId);
                    Assert.AreEqual(projectPath, request.ProjectPath); Assert.AreEqual(installation, request.InstallationRoot);
                    Assert.IsTrue(request.SoftwareRendering);
                    Assert.AreNotEqual(priorSocket, request.SocketPath); priorSocket = request.SocketPath;
                    Assert.IsFalse(File.Exists(request.SocketPath));
                    Assert.AreEqual(record, await File.ReadAllTextAsync(selectionPath, deadline.Token));
                    CollectionAssert.AreEquivalent(files, Directory.GetFiles(installation, "*", SearchOption.AllDirectories));
                    await File.WriteAllTextAsync(Path.Combine(evidence, projectPath.Length == 0 ? "manager-request.json" : "project-request.json"), line, deadline.Token);
                    await process.StandardInput.WriteLineAsync("exit"); process.StandardInput.Close();
                    await process.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, process.ExitCode);
                }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            }

            int failures = 0;
            foreach (string invalid in new[] { "", "{}", new string('x', 4097),
                "{\"schemaVersion\":1,\"schemaVersion\":1,\"selectionId\":\"" + Guid.NewGuid() + "\",\"versionTarget\":\"versions/" + new string('b', 64) + "/payload\"}",
                "{\"schemaVersion\":1,\"selectionId\":\"wrong\",\"versionTarget\":\"versions/" + new string('b', 64) + "/payload\"}" })
            {
                await File.WriteAllTextAsync(selectionPath, invalid, deadline.Token);
                var refused = await WindowsLauncherTests.Invoke(executable, [installation, project, Guid.NewGuid().ToString("D"), instance.ToString("D"), "false"], root, deadline.Token, input: "exit");
                Assert.AreEqual(2, refused.ExitCode); Assert.AreEqual("", refused.Output);
                Assert.AreEqual(invalid, await File.ReadAllTextAsync(selectionPath, deadline.Token)); failures++;
            }
            CollectionAssert.AreEquivalent(files, Directory.GetFiles(installation, "*", SearchOption.AllDirectories));
            string receipt = Path.Combine(evidence, "result.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { schemaVersion = 1, status = "passed",
                nativeRequestBuilderVerified = true, exactKernelIdentity = true, unicodePaths = true,
                selectionRefresh = true, malformedSelectionsRejected = failures, noStoreMutation = true,
                captionJourneyVerified = false }), deadline.Token);
            TestContext.AddResultFile(receipt);
        }
        finally { await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root); }
    }
}
