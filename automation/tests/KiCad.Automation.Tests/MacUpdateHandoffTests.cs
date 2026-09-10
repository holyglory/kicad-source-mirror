using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MacUpdateHandoffTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NonMacHostCannotAcknowledgeMacEditorShutdown()
    {
        if (OperatingSystem.IsMacOS()) { Assert.Inconclusive("Non-Mac refusal control."); return; }
        bool acknowledged = false;
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => MacUpdateHandoff.ExecuteAsync(null!,
            _ => { acknowledged = true; return Task.CompletedTask; }));
        Assert.IsFalse(acknowledged);
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => MacUpdateInspection.InspectAsync("/not-an-install", Guid.NewGuid()));
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => MacUpdateRecovery.RecoverAsync("/not-an-install", Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public async Task NativeMacHandoffCancelsRestartsAndRestoresAfterConfirmedStartupFailure()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("This journey requires real Mac GUI processes and native IPC."); return; }
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        string platform = arm ? "osx-arm64" : "osx-x64";
        string commit = arm ? "f79a41ed7aae781715877ef6c52ccf3762846b84" : "4c6a88502a4c84da7ff1cf4ae9ca54bd266a106d";
        var artifact = new UpdateArtifact(platform, "tar.gz", "kicad-codex-" + commit + "-macos-" + (arm ? "arm64" : "x64") + ".tar.gz",
            arm ? 329413852 : 334286000, arm ? "aa2007dea343d87b5e8945ec22a5a91eee6f3b883ec06634dd93f2c4ea660ad6"
                : "5485b053ba9c18a2113e2e910285a5d7fdb7b73e940fbab12d1b5cd453d7f2b6");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "mac-handoff-fixture-one", commit, [artifact]);
        byte[] envelope = UpdateManifestCodec.Sign(release, key), publicKey = key.ExportSubjectPublicKeyInfo();
        var first = UpdateManifestCodec.Verify(envelope, publicKey, "preview");
        var origin = new Uri("https://kicad.vr.ae/");
        string root = Directory.CreateTempSubdirectory("kicad-mac-handoff-").FullName;
        string installation = Path.Combine(root, "installed");
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "mac-handoff")).FullName;
        var processes = new List<Process>();
        var handoffs = new List<Task<MacUpdateHandoffState>>();
        var streams = new List<Task>();
        string? originalNng = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY");
        // Three real installed-process journeys share this outer ceiling;
        // each individual native readiness handshake still has its 60s limit.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            string probe = await MacProcessIdentityTests.CompileProbe(root, deadline.Token);
            using var downloads = new UpdateDownloader(origin);
            var download = await downloads.DownloadAsync(first, platform, "tar.gz", root, cancellationToken: deadline.Token);
            var installed = await MacVerifiedInstallation.InstallAsync(installation, download.Path, envelope, publicKey, origin, "preview", deadline.Token);
            string target = MacVerifiedInstallation.InspectTarget(installation);
            byte[] nextBytes = UpdateManifestCodec.Sign(release with { Sequence = 2, Version = "mac-handoff-fixture-two" }, key);
            var next = UpdateManifestCodec.Verify(nextBytes, publicKey, "preview");
            _ = await MacVerifiedInstallation.RegisterAsync(installation, target, download.Path, nextBytes, deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", Path.Combine(installed.VersionDirectory, "managed/libnng.dylib"));
            await NngBindingLifetimeTests.RunProbe(Path.Combine(installed.VersionDirectory, "managed/libnng.dylib"), deadline.Token);
            var environment = new Dictionary<string, string?>
            {
                ["KICAD_CONFIG_HOME"] = Path.Combine(root, "config"), ["KICAD_CACHE_HOME"] = Path.Combine(root, "cache"),
                ["XDG_CONFIG_HOME"] = Path.Combine(root, "xdg-config"), ["XDG_CACHE_HOME"] = Path.Combine(root, "xdg-cache")
            };
            foreach (string scenario in new[] { "restart", "restore", "interrupted" })
            {
                bool failStartup = scenario == "restore";
                string name = scenario;
                string oldSocket = "/tmp/km-" + Guid.NewGuid().ToString("N")[..12] + ".sock";
                string newSocket = "/tmp/km-" + Guid.NewGuid().ToString("N")[..12] + ".sock";
                Guid instance = Guid.NewGuid();
                var start = new ProcessStartInfo(installed.NativeExecutable)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                foreach (var item in environment) start.Environment[item.Key] = item.Value;
                start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
                foreach (string argument in new[] { "--new", "--update-manager", instance.ToString("D"), "--api-socket", oldSocket,
                    "--automation-log", Path.Combine(evidence, name + "-old-native.log") }) start.ArgumentList.Add(argument);
                var old = Process.Start(start)!; processes.Add(old);
                streams.Add(Capture(old.StandardOutput.BaseStream, Path.Combine(evidence, name + "-old.stdout.log")));
                streams.Add(Capture(old.StandardError.BaseStream, Path.Combine(evidence, name + "-old.stderr.log")));
                var client = new NativeClient(new NngTransport(), "ipc://" + oldSocket);
                var session = await Ready(client, old, deadline.Token);
                Assert.AreEqual("", session.ProjectPath);
                var identity = MacProcessIdentity.Read(old.Id);
                target = MacVerifiedInstallation.InspectTarget(installation);
                var request = new MacUpdateHandoffRequest(installation, target, next.PayloadSha256, Guid.NewGuid(), identity,
                    "", instance, newSocket, false);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacUpdateHandoff.ExecuteAsync(
                    request with { OldProcess = identity with { StartMicroseconds = (identity.StartMicroseconds + 1) % 1000000 } },
                    _ => throw new AssertFailedException("Stale process was acknowledged"), deadline.Token));
                if (scenario == "interrupted")
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => MacUpdateHandoff.ExecuteAsync(request,
                        _ => throw new IOException("Synthetic lost acknowledgement before launch."), deadline.Token, environment));
                    Assert.AreEqual("original_running", (await MacUpdateInspection.InspectAsync(installation, request.OperationId, deadline.Token)).Status);
                    Assert.AreEqual("original_running", (await MacUpdateRecovery.RecoverAsync(installation, request.OperationId,
                        Guid.NewGuid(), deadline.Token)).Status);
                    await MacProcessIdentityTests.Run(probe, ["quit", old.Id.ToString()], deadline.Token);
                    await old.WaitForExitAsync(deadline.Token);
                    Assert.AreEqual("no_replacement_recorded", (await MacUpdateInspection.InspectAsync(installation, request.OperationId, deadline.Token)).Status);
                    Guid recoveryId = Guid.NewGuid();
                    var recovery = await MacUpdateRecovery.RecoverAsync(installation, request.OperationId, recoveryId, deadline.Token);
                    Assert.AreEqual("restored", recovery.Status, recovery.State?.Error);
                    Assert.IsNotNull(recovery.State?.ProcessId);
                    using var recovered = Process.GetProcessById(recovery.State.ProcessId.Value);
                    processes.Add(Process.GetProcessById(recovered.Id));
                    Assert.AreEqual("replacement_running", (await MacUpdateInspection.InspectAsync(installation, request.OperationId, deadline.Token)).Status);
                    var retried = await MacUpdateRecovery.RecoverAsync(installation, request.OperationId, recoveryId, deadline.Token);
                    Assert.AreEqual("attempt_already_recorded", retried.Status);
                    Assert.IsTrue(retried.Reused);
                    Assert.AreEqual(target, MacVerifiedInstallation.InspectTarget(installation));
                    await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1, platform, commit, explicitRecoveryVerified = true, liveOriginalNotDuplicated = true,
                        retryDidNotDuplicate = true, sharedSelectionPreserved = true, actualProcessIdentityVerified = true,
                        automaticUpdatingQualified = false
                    }), deadline.Token);
                    await MacProcessIdentityTests.Run(probe, ["quit", recovered.Id.ToString()], deadline.Token);
                    await recovered.WaitForExitAsync(deadline.Token);
                    continue;
                }
                using (var cancel = new CancellationTokenSource())
                {
                    var cancelled = await MacUpdateHandoff.ExecuteAsync(request with { OperationId = Guid.NewGuid() },
                        _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
                    Assert.AreEqual("cancelled_before_activation", cancelled.Status);
                    Assert.IsFalse(old.HasExited);
                }
                var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var handoff = MacUpdateHandoff.ExecuteAsync(request, state =>
                { if (state.Status == "waiting_for_exit") acknowledged.TrySetResult(); return Task.CompletedTask; },
                    environment, failStartup ? (label, launch) =>
                    {
                        if (label == "candidate") launch.ArgumentList.Add(Path.Combine(root, "missing.kicad_pro"));
                    } : null, deadline.Token, async (_, launched) =>
                    { processes.Add(Process.GetProcessById(launched.Id)); await Task.CompletedTask; });
                handoffs.Add(handoff);
                await Task.WhenAny(acknowledged.Task, handoff).WaitAsync(deadline.Token);
                if (handoff.IsCompleted) await handoff;
                await acknowledged.Task.WaitAsync(deadline.Token);
                Assert.IsFalse(old.HasExited);
                Assert.AreEqual("operation_unavailable", (await MacUpdateInspection.InspectAsync(installation, request.OperationId, deadline.Token)).Status);
                await MacProcessIdentityTests.Run(probe, ["quit", old.Id.ToString()], deadline.Token);
                var result = await handoff.WaitAsync(deadline.Token);
                Assert.AreEqual(failStartup ? "restored" : "restarted", result.Status, result.Error);
                Assert.IsNotNull(result.ProcessIdentity);
                Assert.AreEqual(result.ProcessIdentity, MacProcessIdentity.Read(result.ProcessId!.Value));
                Assert.AreNotEqual(session.Epoch, result.NativeEpoch);
                var fresh = await new NativeClient(new NngTransport(), result.Endpoint!).HandshakeAsync(deadline.Token);
                Assert.AreEqual("", fresh.ProjectPath);
                var replay = await MacUpdateHandoff.ExecuteAsync(request, _ => throw new AssertFailedException("Retry acknowledged another close"), deadline.Token);
                Assert.AreEqual("reconciliation_required", replay.Status);
                Assert.AreEqual("replacement_running", (await MacUpdateInspection.InspectAsync(installation, request.OperationId, deadline.Token)).Status);
                Assert.AreEqual("launch_outcome_not_recoverable", (await MacUpdateRecovery.RecoverAsync(installation,
                    request.OperationId, Guid.NewGuid(), deadline.Token)).Status);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, platform, commit, result.Status, cancelledBeforeClose = true, wrongProcessRejected = true,
                    emptyManagerPreserved = true, nativeEpochChanged = true, retryDidNotDuplicate = true,
                    restorationVerified = failStartup, captionClickVerified = false, dirtySaveJourneyVerified = false,
                    automaticUpdatingQualified = false
                }), deadline.Token);
                using var replacement = Process.GetProcessById(result.ProcessId.Value);
                await MacProcessIdentityTests.Run(probe, ["quit", result.ProcessId.Value.ToString()], deadline.Token);
                await replacement.WaitForExitAsync(deadline.Token);
            }
        }
        finally
        {
            deadline.Cancel();
            foreach (var process in processes)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            foreach (var handoff in handoffs)
                try { await handoff; } catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ArgumentException) { }
            try { await Task.WhenAll(streams); } catch (IOException) { }
            if (Directory.Exists(Path.Combine(installation, "handovers")))
                foreach (string path in Directory.GetFiles(Path.Combine(installation, "handovers"), "*.json", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(evidence, "handovers", Path.GetRelativePath(Path.Combine(installation, "handovers"), path));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
                }
            foreach (string path in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(path);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", originalNng);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<KiCad.Automation.Protocol.AutomationSession> Ready(NativeClient client, Process process, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (process.HasExited) throw new AssertFailedException("Native Mac manager exited " + process.ExitCode);
            try { return await client.HandshakeAsync(deadline.Token); }
            catch (NngException) { }
            catch (NativeApiException error) when (error.Status is 4 or 7) { }
            await Task.Delay(100, deadline.Token);
        }
    }
    private static async Task Capture(Stream source, string path)
    {
        await using var file = File.Create(path);
        await source.CopyToAsync(file);
    }
}
