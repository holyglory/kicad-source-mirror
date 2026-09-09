using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyUpdateRestartHandoff(InstalledLinuxUpdate installed, InstalledLinuxUpdate candidate,
        string evidence, CancellationToken token, bool rejectCandidateStartup = false, Func<Task>? beforeHandoff = null,
        bool rejectActivation = false, bool changeSelectionDuringClose = false, bool useInstalledHelper = false)
    {
        string temporary = Directory.CreateTempSubdirectory("kicad-restart-").FullName;
        var processes = new List<Process>();
        var captures = new List<Task>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(beforeHandoff is null ? 90 : 180));
        using var handoffCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<UpdateHandoffState>? handoff = null;
        try
        {
            var displayStart = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" }) displayStart.ArgumentList.Add(arg);
            Process display = Process.Start(displayStart)!;
            processes.Add(display);
            captures.Add(Capture(display.StandardError, Path.Combine(evidence, "restart-xvfb.log")));
            string fixtureDisplay = ":" + await display.StandardOutput.ReadLineAsync(deadline.Token);
            string project = Path.Combine(temporary, "fixture.kicad_pro");
            string schematic = Path.Combine(temporary, "fixture.kicad_sch");
            Guid root = Guid.NewGuid();
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
            {
                meta = new { version = 3 }, schematic = new { top_level_sheets = new[]
                { new { uuid = root.ToString("D"), name = "fixture", filename = "fixture.kicad_sch" } } }
            }), deadline.Token);
            string prefix = Path.Combine(installed.VersionDirectory, "runtime");
            string socket = Path.Combine(temporary, "old.sock");
            string instance = Guid.NewGuid().ToString("D");
            var start = new ProcessStartInfo(Path.Combine(prefix, "bin/kicad"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temporary };
            start.Environment["DISPLAY"] = fixtureDisplay;
            start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
            start.Environment.Remove("APPDIR");
            start.Environment["LD_LIBRARY_PATH"] = Path.Combine(prefix, "lib");
            start.Environment["KICAD_STOCK_DATA_HOME"] = Path.Combine(prefix, "share/kicad");
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(temporary, "config");
            start.Environment["KICAD_CACHE_HOME"] = Path.Combine(temporary, "cache");
            foreach (string arg in new[] { "--new", "--automation", instance, "--api-socket", socket,
                "--automation-log", Path.Combine(evidence, "old-native.log"), "--software-rendering", project }) start.ArgumentList.Add(arg);
            Process old = Process.Start(start)!;
            processes.Add(old);
            captures.Add(Capture(old.StandardOutput, Path.Combine(evidence, "old.stdout.log")));
            captures.Add(Capture(old.StandardError, Path.Combine(evidence, "old.stderr.log")));
            var client = new NativeClient(new NngTransport(), "ipc://" + socket);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(old.HasExited);
                try { await client.HandshakeAsync(deadline.Token); break; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, deadline.Token);
            }
            await client.CreateRootSchematicAsync(schematic, deadline.Token);
            string oldEpoch = client.Epoch;
            if (beforeHandoff is not null)
            {
                await beforeHandoff().WaitAsync(deadline.Token);
                Assert.IsFalse(old.HasExited, "Updating another instance must preserve this live editor.");
                Assert.AreEqual(oldEpoch, (await client.HandshakeAsync(deadline.Token)).Epoch);
            }
            string selectedTarget = LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory);
            string replacementSocket = Path.Combine(temporary, "new.sock");
            var request = new LinuxUpdateHandoffRequest(installed.Root, selectedTarget, candidate.ManifestSha256,
                Guid.NewGuid(), LinuxProcessIdentity.Read(old.Id), project, Guid.Parse(instance), replacementSocket, true);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxUpdateHandoff.ExecuteAsync(
                request with { OldProcess = request.OldProcess with { StartTicks = request.OldProcess.StartTicks + 1 } }, _ => Task.CompletedTask, deadline.Token));
            if (beforeHandoff is not null)
            {
                string oldReadme = Path.Combine(installed.VersionDirectory, "README.txt");
                byte[] oldBytes = await File.ReadAllBytesAsync(oldReadme, deadline.Token);
                try
                {
                    await File.AppendAllTextAsync(oldReadme, "Synthetic drift of retained old version.", deadline.Token);
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxUpdateHandoff.ExecuteAsync(
                        request, _ => Task.CompletedTask, deadline.Token));
                    Assert.IsFalse(old.HasExited);
                    Assert.AreEqual(selectedTarget, LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory));
                }
                finally { await File.WriteAllBytesAsync(oldReadme, oldBytes, deadline.Token); }
            }
            string candidateReadme = Path.Combine(candidate.VersionDirectory, "README.txt");
            byte[] originalReadme = await File.ReadAllBytesAsync(candidateReadme, deadline.Token);
            await File.AppendAllTextAsync(candidateReadme, "Synthetic preflight drift.", deadline.Token);
            bool incorrectlyAcknowledged = false;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxUpdateHandoff.ExecuteAsync(request, _ =>
            {
                incorrectlyAcknowledged = true;
                return Task.CompletedTask;
            }, deadline.Token));
            Assert.IsFalse(incorrectlyAcknowledged);
            Assert.IsFalse(old.HasExited);
            Assert.AreEqual(selectedTarget, LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory));
            await File.WriteAllBytesAsync(candidateReadme, originalReadme, deadline.Token);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The handoff process normally inherits the native manager display
            // and configuration. Test-specific launch context is passed directly,
            // without mutating the test host's process environment.
            handoff = rejectCandidateStartup ? LinuxUpdateHandoff.ExecuteAsync(request, state =>
            {
                if (state.Status == "waiting_for_exit") waiting.TrySetResult();
                return Task.CompletedTask;
            }, start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value), (label, launch) =>
            {
                // Exercise a real native startup rejection, not a fake readiness
                // reply: the candidate receives an absent explicit project.
                if (rejectCandidateStartup && label == "candidate")
                    launch.ArgumentList[^1] = Path.Combine(temporary, "absent.kicad_pro");
            }, handoffCancellation.Token) : RunHandoffProcess();
            await Task.WhenAny(waiting.Task, handoff).WaitAsync(deadline.Token);
            if (handoff.IsCompleted) await handoff; // surface a rejected request immediately
            await waiting.Task.WaitAsync(deadline.Token);
            NativeKeyboard.SchematicShortcut(fixtureDisplay, old.Id, "q", "KiCad", true, false);
            while (!NativeKeyboard.HasWindow(fixtureDisplay, old.Id, "Save"))
                await Task.Delay(100, deadline.Token);
            await NativeKeyboard.CaptureAsync(fixtureDisplay, Path.Combine(evidence, "dirty-close.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(fixtureDisplay, old.Id, "Escape", "Save", false, false);
            while (NativeKeyboard.HasWindow(fixtureDisplay, old.Id, "Save")) await Task.Delay(100, deadline.Token);
            Assert.IsFalse(old.HasExited);
            Assert.IsFalse(handoff.IsCompleted);
            Assert.AreEqual(selectedTarget, LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory));
            NativeKeyboard.SchematicShortcut(fixtureDisplay, old.Id, "s");
            while (!File.Exists(schematic)) await Task.Delay(100, deadline.Token);
            byte[] savedSchematic = await File.ReadAllBytesAsync(schematic, deadline.Token);
            string selectionToPreserve = selectedTarget;
            byte[] acceptedBeforeClose = await File.ReadAllBytesAsync(Path.Combine(installed.Root, "state", "accepted-envelope.json"), deadline.Token);
            if (rejectActivation)
                await File.AppendAllTextAsync(candidateReadme, "Synthetic candidate drift after the accepted preflight.", deadline.Token);
            if (changeSelectionDuringClose)
            {
                var concurrent = await LinuxVerifiedInstallation.ActivateAsync(installed.Root, selectedTarget,
                    candidate.ManifestSha256, Guid.NewGuid(), deadline.Token);
                selectionToPreserve = concurrent.Activation.Target;
                Assert.AreNotEqual(selectedTarget, selectionToPreserve);
            }
            NativeKeyboard.SchematicShortcut(fixtureDisplay, old.Id, "q", "KiCad", true, false);
            await old.WaitForExitAsync(deadline.Token);
            var result = await handoff;
            await File.WriteAllTextAsync(Path.Combine(evidence, "restart-result.json"), JsonSerializer.Serialize(result), deadline.Token);
            if (rejectActivation) await File.WriteAllBytesAsync(candidateReadme, originalReadme, deadline.Token);
            bool expectRestored = rejectCandidateStartup || rejectActivation || changeSelectionDuringClose;
            Assert.AreEqual(expectRestored ? "restored" : "restarted", result.Status, result.Error);
            Assert.IsNotNull(result.ProcessId);
            Process replacement = Process.GetProcessById(result.ProcessId.Value);
            processes.Add(replacement);
            Assert.IsFalse(replacement.HasExited);
            Assert.AreNotEqual(oldEpoch, result.NativeEpoch);
            Assert.AreEqual("reconciliation_required", (await LinuxUpdateHandoff.ExecuteAsync(request, _ => Task.CompletedTask, deadline.Token)).Status);
            var replacementClient = new NativeClient(new NngTransport(), "ipc://" + replacementSocket
                + (expectRestored ? ".r" : ""), result.NativeEpoch);
            await replacementClient.OpenRootSchematicAsync(schematic, deadline.Token);
            CollectionAssert.AreEqual(savedSchematic, await File.ReadAllBytesAsync(schematic, deadline.Token));
            if (rejectActivation || changeSelectionDuringClose)
            {
                Assert.AreEqual(selectionToPreserve, LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory));
                CollectionAssert.AreEqual(acceptedBeforeClose,
                    await File.ReadAllBytesAsync(Path.Combine(installed.Root, "state", "accepted-envelope.json"), deadline.Token));
                Assert.AreEqual(Path.Combine(installed.VersionDirectory, "runtime/bin/kicad"), replacement.MainModule!.FileName);
                Assert.IsTrue(File.Exists(Path.Combine(result.JournalDirectory, "activation-failure.json")));
            }
            await NativeKeyboard.CaptureAsync(fixtureDisplay, Path.Combine(evidence, "replacement.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(fixtureDisplay, replacement.Id, "q", "KiCad", true, false);
            await replacement.WaitForExitAsync(deadline.Token);
            string journalEvidence = Directory.CreateDirectory(Path.Combine(evidence, "journal")).FullName;
            foreach (string file in Directory.GetFiles(result.JournalDirectory))
                File.Copy(file, Path.Combine(journalEvidence, Path.GetFileName(file)));

            async Task<UpdateHandoffState> RunHandoffProcess()
            {
                string configuration = Path.Combine(temporary, "restart-request.json");
                await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new LinuxRestartConfiguration(1, request),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
                var runner = useInstalledHelper
                    ? new ProcessStartInfo(Path.Combine(installed.VersionDirectory, "kicad-mcp"))
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
                    : UpdateCommandTests.StartInfo();
                await File.WriteAllTextAsync(Path.Combine(evidence, "handoff-helper.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, helperKind = useInstalledHelper ? "installed" : "source",
                    executable = runner.FileName, installedCommit = installed.Commit
                }), deadline.Token);
                foreach (string argument in new[] { "--restart-update", "--configuration", configuration }) runner.ArgumentList.Add(argument);
                foreach (var (name, value) in start.Environment) runner.Environment[name] = value;
                using var helper = Process.Start(runner)!;
                Task<string> errors = helper.StandardError.ReadToEndAsync(handoffCancellation.Token);
                try
                {
                    string readyLine = (await helper.StandardOutput.ReadLineAsync(handoffCancellation.Token))!;
                    await File.WriteAllTextAsync(Path.Combine(evidence, "handoff-ready.json"), readyLine, deadline.Token);
                    using var ready = JsonDocument.Parse(readyLine);
                    Assert.IsTrue(ready.RootElement.TryGetProperty("state", out var accepted), readyLine);
                    Assert.AreEqual("waiting_for_exit", accepted.GetProperty("status").GetString());
                    waiting.TrySetResult();
                    // Model the old window disappearing: close its response pipe
                    // after the acknowledgement, before it saves/closes.
                    helper.StandardOutput.Close();
                    await helper.WaitForExitAsync(handoffCancellation.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "handoff.stderr.log"), await errors, deadline.Token);
                    string journal = ready.RootElement.GetProperty("state").GetProperty("journalDirectory").GetString()!;
                    var result = JsonSerializer.Deserialize<UpdateHandoffState>(await File.ReadAllBytesAsync(Path.Combine(journal, "state.json"), deadline.Token),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    await File.WriteAllTextAsync(Path.Combine(evidence, "handoff-result.json"), JsonSerializer.Serialize(result), deadline.Token);
                    Assert.AreEqual(0, helper.ExitCode, result.Error);
                    return result;
                }
                finally
                {
                    if (!helper.HasExited) helper.Kill(entireProcessTree: true);
                    await helper.WaitForExitAsync();
                    await errors;
                }
            }
        }
        finally
        {
            handoffCancellation.Cancel();
            if (handoff is not null)
            {
                try
                {
                    var final = await handoff;
                    if (final.ProcessId is int pid && processes.All(process => process.Id != pid))
                    {
                        try
                        {
                            var owned = Process.GetProcessById(pid);
                            if (!owned.HasExited && LinuxProcessIdentity.Read(owned.Id) == final.ProcessIdentity) processes.Add(owned);
                            else owned.Dispose();
                        }
                        catch (ArgumentException) { }
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { } // preserve the original assertion and still clean owned processes
            }
            foreach (Process process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await Task.WhenAll(captures);
            Directory.Delete(temporary, recursive: true);
        }
    }
}
