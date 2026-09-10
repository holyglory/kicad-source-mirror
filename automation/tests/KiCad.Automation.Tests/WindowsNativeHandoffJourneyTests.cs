using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class WindowsNativeHandoffJourneyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsHandoff")]
    public async Task IsolatedNativeHostVerifiesRestartAndOlderEditorRecovery()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows KiCad."); return; }
        string root = Directory.CreateTempSubdirectory("kwrealhandoff-").FullName;
        string evidence = Evidence();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "vstest", typeof(WindowsNativeHandoffJourneyTests).Assembly.Location,
                "--TestCaseFilter:FullyQualifiedName=KiCad.Automation.Tests.WindowsNativeHandoffJourneyTests.NativeHostChild",
                "--Logger:trx;LogFileName=child.trx", "--ResultsDirectory:" + Path.Combine(evidence, "child-results") }) start.ArgumentList.Add(argument);
            start.Environment["KICAD_WINDOWS_HANDOFF_CHILD"] = "true";
            start.Environment["KICAD_WINDOWS_HANDOFF_ROOT"] = root;
            start.Environment["KICAD_WINDOWS_HANDOFF_EVIDENCE"] = evidence;
            using var process = Process.Start(start)!;
            await using var stdout = File.Create(Path.Combine(evidence, "child.stdout.log"));
            await using var stderr = File.Create(Path.Combine(evidence, "child.stderr.log"));
            var output = process.StandardOutput.BaseStream.CopyToAsync(stdout); var error = process.StandardError.BaseStream.CopyToAsync(stderr);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(); await Task.WhenAll(output, error);
            }
            Assert.AreEqual(0, process.ExitCode, "See retained isolated native test results.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "result.json"), deadline.Token));
            Assert.AreEqual("passed", result.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            // The native NNG binding lives only in the now-exited child host, so
            // its process-lifetime DLL handle cannot lock the retained version tree.
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
        }
    }

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("InternalWindowsHandoff")]
    public async Task NativeHostChild()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("KICAD_WINDOWS_HANDOFF_CHILD") != "true")
        { Assert.Inconclusive("Called only by the isolated Windows handoff parent."); return; }
        string root = Environment.GetEnvironmentVariable("KICAD_WINDOWS_HANDOFF_ROOT")!;
        string evidence = Environment.GetEnvironmentVariable("KICAD_WINDOWS_HANDOFF_EVIDENCE")!;
        Assert.IsTrue(Path.IsPathFullyQualified(root) && Path.IsPathFullyQualified(evidence));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var job = new WindowsProcessJob();
        var processes = new List<Process>(); var captures = new List<Task>();
        try
        {
            var input = await WindowsPackageStagingTests.Inputs(deadline.Token);
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "native-handoff-fixture-one", input.Commit, [input.Artifact]);
            string installation = Path.Combine(root, "installed");
            var initial = await WindowsVerifiedVersions.CreateAsync(installation, input.Path, UpdateManifestCodec.Sign(release, publisher),
                publisher.ExportSubjectPublicKeyInfo(), new Uri("https://fixture.invalid/"), "preview", deadline.Token);
            var candidate = await WindowsVerifiedVersions.RegisterAsync(installation, initial.SelectionId, input.Path,
                UpdateManifestCodec.Sign(release with { Sequence = 2, Version = "native-handoff-fixture-two" }, publisher), deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", Path.Combine(initial.Version.VersionDirectory, "bin/nng.dll"));
            var environment = new Dictionary<string, string?>
            { ["KICAD_CONFIG_HOME"] = Path.Combine(root, "config"), ["KICAD_CACHE_HOME"] = Path.Combine(root, "cache") };
            Guid instance = Guid.NewGuid();
            var old = await Start(initial.Version, instance, "original");
            var request = Request(old.Process, initial.SelectionId, instance);
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = await WindowsUpdateHandoff.ExecuteAsync(request, state =>
                { Assert.AreEqual("waiting_for_exit", state.Status); cancel.Cancel(); return Task.CompletedTask; },
                    environment, null, cancel.Token, OwnReplacement);
                Assert.AreEqual("cancelled_before_activation", cancelled.Status);
                Assert.IsFalse(old.Process.HasExited);
                Assert.AreEqual(initial, await WindowsVerifiedVersions.InspectCurrentAsync(installation, deadline.Token));
            }
            request = request with { OperationId = Guid.NewGuid(), SocketPath = Pipe() };
            var restarted = await WindowsUpdateHandoff.ExecuteAsync(request, _ => Close(old.Process), environment, null, deadline.Token, OwnReplacement);
            Assert.AreEqual("restarted", restarted.Status, restarted.Error);
            Assert.IsNotNull(restarted.ProcessIdentity); Assert.AreNotEqual(old.Epoch, restarted.NativeEpoch);
            Assert.AreEqual(candidate.Version.NativeExecutable, restarted.ProcessIdentity.Executable, ignoreCase: true);
            var current = await WindowsVerifiedVersions.InspectCurrentAsync(installation, deadline.Token);
            Assert.AreEqual(candidate.Version, current.Version);
            using (var replacement = Process.GetProcessById(restarted.ProcessId!.Value))
            { await Close(replacement); await replacement.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, replacement.ExitCode); }

            // The running editor can legitimately be older than the globally
            // selected version. A failed update must reopen that actual version.
            instance = Guid.NewGuid();
            var older = await Start(initial.Version, instance, "older");
            var recoverRequest = Request(older.Process, current.SelectionId, instance);
            var recovered = await WindowsUpdateHandoff.ExecuteAsync(recoverRequest, _ => Close(older.Process), environment,
                (label, start) => { if (label == "candidate") start.FileName = Path.Combine(root, "deliberately-missing.exe"); },
                deadline.Token, OwnReplacement);
            Assert.AreEqual("restored", recovered.Status, recovered.Error);
            Assert.IsNotNull(recovered.ProcessIdentity);
            Assert.AreEqual(initial.Version.NativeExecutable, recovered.ProcessIdentity.Executable, ignoreCase: true);
            Assert.AreNotEqual(older.Epoch, recovered.NativeEpoch);
            Assert.AreEqual(candidate.Version, (await WindowsVerifiedVersions.InspectCurrentAsync(installation, deadline.Token)).Version);
            using (var replacement = Process.GetProcessById(recovered.ProcessId!.Value))
            { await Close(replacement); await replacement.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, replacement.ExitCode); }
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", nativeCommit = input.Commit, archiveSha256 = input.Artifact.Sha256,
                realKiCadProcesses = true, isolatedTestPublisher = true, syntheticReleaseSequence = true,
                cancellationPreservedOriginal = true, nativeRestartVerified = true, restoredOlderLiveVersion = true,
                selectedVersionPreserved = true, automaticUpdatingQualified = false, fullDirtyCaptionJourneyVerified = false
            }), deadline.Token);

            WindowsUpdateHandoffRequest Request(Process process, string selection, Guid id) => new(installation, selection,
                candidate.Version.ManifestSha256, Guid.NewGuid(), WindowsProcessIdentity.Read(process.Id), "", id, Pipe(), true);

            async Task<(Process Process, string Epoch)> Start(VerifiedWindowsVersion version, Guid id, string label)
            {
                string socket = Pipe();
                var start = new ProcessStartInfo(version.NativeExecutable) { UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                foreach (var (name, value) in environment) start.Environment[name] = value;
                start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
                foreach (string argument in new[] { "--new", "--update-manager", id.ToString("D"), "--api-socket", socket,
                    "--automation-log", Path.Combine(evidence, label + "-native.log"), "--software-rendering" }) start.ArgumentList.Add(argument);
                var process = Process.Start(start)!; processes.Add(process); job.Attach(process);
                captures.Add(Capture(process.StandardOutput.BaseStream, Path.Combine(evidence, label + ".stdout.log")));
                captures.Add(Capture(process.StandardError.BaseStream, Path.Combine(evidence, label + ".stderr.log")));
                var client = new NativeClient(new NngTransport(), NativeIpcEndpoint.FromSocketPath(socket));
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); readiness.CancelAfter(TimeSpan.FromSeconds(60));
                while (true)
                {
                    Assert.IsFalse(process.HasExited, "Real native manager exited before readiness.");
                    try
                    {
                        var session = await client.HandshakeAsync(readiness.Token);
                        Assert.AreEqual(id.ToString("D"), session.InstanceId); Assert.AreEqual("", session.ProjectPath);
                        return (process, session.Epoch);
                    }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(250, readiness.Token);
                }
            }
            async Task Close(Process process)
            {
                Assert.IsTrue(job.Contains(process));
                nint window = await WindowsNativeUi.WaitForWindow(process, "KiCad", deadline.Token);
                WindowsNativeUi.Capture(process, window, Path.Combine(evidence, "manager-" + process.Id + ".png"));
                WindowsNativeUi.Close(process, window);
            }
            Task OwnReplacement(string label, Process process)
            {
                try { job.Attach(process); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                return Task.CompletedTask;
            }
        }
        finally
        {
            job.Dispose();
            foreach (var process in processes) { await process.WaitForExitAsync(); process.Dispose(); }
            await Task.WhenAll(captures);
            string handovers = Path.Combine(root, "installed/handovers");
            if (Directory.Exists(handovers))
                foreach (string file in Directory.GetFiles(handovers, "*", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(evidence, "handovers", Path.GetRelativePath(handovers, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
                }
        }
    }

    private string Evidence() => Directory.CreateDirectory(Path.Combine(
        Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!, "windows-native-handoff")).FullName;
    private static string Pipe() => Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "kwh-" + Guid.NewGuid().ToString("N") + ".sock");
    private static async Task Capture(Stream input, string path)
    { await using var output = File.Create(path); await input.CopyToAsync(output); }
}
