using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Focused rendered exclusion journey, not complete ERC/settings revision
// coverage. Comment editing and severity journeys remain separate checks.
[TestClass, TestCategory("ExternalIntegration"), TestCategory("NativeErcDialog")]
public sealed class NativeErcDialogTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ExcludingAnActualViolationInvalidatesStaleEditsAndPreservesTheOtherProject()
    {
        Assert.IsTrue(OperatingSystem.IsLinux(), "This is a Linux native-display fixture, not Mac evidence.");
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "automation/KiCad.Automation.slnx")))
            source = source.Parent;
        Assert.IsNotNull(source);
        string executable = Environment.GetEnvironmentVariable("KICAD_ERC_NATIVE_EXECUTABLE")
            ?? Path.Combine(source.FullName, "automation/artifacts/native/kicad/kicad");
        Assert.IsTrue(Path.IsPathFullyQualified(executable) && File.Exists(executable));
        string evidence = Directory.CreateDirectory(Path.Combine(TestContext.TestResultsDirectory!, "erc-dialog")).FullName;
        string scratch = Directory.CreateTempSubdirectory("kicad-erc-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var processes = new List<Process>();
        var logs = new List<Task>();
        string? display = null;
        try
        {
            var startDisplay = new ProcessStartInfo("Xvfb")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
                startDisplay.ArgumentList.Add(argument);
            var displayProcess = Process.Start(startDisplay)!;
            processes.Add(displayProcess);
            logs.Add(Capture(displayProcess.StandardError, Path.Combine(evidence, "display.stderr.log")));
            string? number = await displayProcess.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.IsTrue(int.TryParse(number, out _), "Xvfb must allocate its own display.");
            display = ":" + number;
            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(scratch, "registry"));
            var identities = new List<ErcDesign>();

            for (int index = 0; index < 2; ++index)
            {
                string directory = Directory.CreateDirectory(Path.Combine(scratch, index.ToString())).FullName;
                string project = Path.Combine(directory, "erc.kicad_pro");
                string schematic = Path.ChangeExtension(project, ".kicad_sch");
                await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
                File.Copy(Path.Combine(source.FullName, "qa/data/eeschema/erc_pin_not_connected_basic.kicad_sch"), schematic);
                string instanceId = Guid.NewGuid().ToString("D");
                string endpoint = Path.Combine(directory, "api.sock");
                var start = new ProcessStartInfo(executable)
                { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["DISPLAY"] = display;
                start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
                start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
                start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
                foreach (string argument in new[] { "--automation", instanceId, "--api-socket", endpoint,
                    "--automation-log", Path.Combine(evidence, $"{index}-native.log"), "--software-rendering", project })
                    start.ArgumentList.Add(argument);
                var process = Process.Start(start)!;
                processes.Add(process);
                logs.Add(Capture(process.StandardOutput, Path.Combine(evidence, $"{index}-stdout.log")));
                logs.Add(Capture(process.StandardError, Path.Combine(evidence, $"{index}-stderr.log")));
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                startup.CancelAfter(TimeSpan.FromSeconds(20));
                int backoff = 25;
                while (true)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    Assert.IsFalse(process.HasExited, "The isolated editor exited during startup.");
                    try { await registry.AttachAsync("ipc://" + endpoint, instanceId, startup.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(backoff, startup.Token); backoff = Math.Min(backoff * 2, 500);
                }
                var client = registry.Client(instanceId);
                var version = (await client.GetVersionAsync(deadline.Token)).Version;
                string config = Directory.CreateDirectory(Path.Combine(directory, "config/kicad", $"{version.Major}.{version.Minor}")).FullName;
                await File.WriteAllTextAsync(Path.Combine(config, "user.hotkeys"),
                    "eeschema.InspectionTool.runERC\tCtrl+F11\t\n", deadline.Token);
                var opened = await client.OpenRootSchematicAsync(schematic, deadline.Token);
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = opened.Document }, deadline.Token);
                var baseline = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = opened.Document }, deadline.Token);
                identities.Add(new(instanceId, client, baseline, process.Id, opened.Document, project));
                NativeKeyboard.SchematicShortcut(display, process.Id, "F11");
                using var visible = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                visible.CancelAfter(TimeSpan.FromSeconds(10));
                backoff = 25;
                while (!NativeKeyboard.HasWindow(display, process.Id, "Electrical Rules Checker"))
                { await Task.Delay(backoff, visible.Token); backoff = Math.Min(backoff * 2, 500); }
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{index}-erc-open.png"), deadline.Token);
                // Opening the native checker focuses its real Run ERC button.
                NativeKeyboard.SchematicShortcut(display, process.Id, "Return", "Electrical Rules Checker", false, false);
                var after = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = opened.Document }, deadline.Token);
                Assert.AreEqual(baseline.DocumentEpoch, after.DocumentEpoch);
                Assert.AreEqual(baseline.Sequence, after.Sequence, "Running checks is not an override edit.");
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"{index}-erc-run.png"), deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(evidence, $"{index}-observation.json"), JsonSerializer.Serialize(new
                {
                    instanceId, processId = process.Id, project, document = opened.Document.ToString(),
                    baseline.DocumentEpoch, baseline.Sequence, dialogObserved = true,
                    overrideEditingQualified = false, trackingComplete = false
                }), deadline.Token);
            }
            Assert.AreNotEqual(identities[0].Baseline.DocumentEpoch, identities[1].Baseline.DocumentEpoch);
            Assert.AreEqual(2, registry.List().Count);
            foreach (var item in identities)
                Assert.AreEqual(item.Id, (await item.Client.HandshakeAsync(deadline.Token)).InstanceId);

            var first = identities[0];
            var other = identities[1];
            var originalTitle = await first.Client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = first.Document }, deadline.Token);
            byte[] otherProject = await File.ReadAllBytesAsync(other.Project, deadline.Token);
            var otherSaveState = await other.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = other.Document }, deadline.Token);

            // These positions are grounded in the retained 1280x900 GTK dialog
            // probe. Use its visible All filter, first violation and real menu.
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 75, clickFromBottom: 72);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "click", "Electrical Rules Checker", false,
                clickFromLeft: 250, clickFromTop: 85);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "selected-violation.png"), deadline.Token);
            NativeKeyboard.SchematicShortcut(display, first.ProcessId, "right-click", "Electrical Rules Checker", false,
                clickFromLeft: 250, clickFromTop: 85);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "exclusion-menu.png"), deadline.Token);
            foreach (string key in new[] { "Home", "Return" })
                NativeKeyboard.SchematicShortcut(display, first.ProcessId, key, "Electrical Rules Checker", false, false);
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            changed.CancelAfter(TimeSpan.FromSeconds(10));
            SchematicChangeJournal journal;
            int changeBackoff = 25;
            while (true)
            {
                journal = await first.Client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = first.Document, DocumentEpoch = first.Baseline.DocumentEpoch,
                        AfterSequence = first.Baseline.Sequence }, changed.Token);
                if (journal.Sequence != first.Baseline.Sequence) break;
                await Task.Delay(changeBackoff, changed.Token); changeBackoff = Math.Min(changeBackoff * 2, 500);
            }
            Assert.AreEqual(first.Baseline.Sequence + 1, journal.Sequence);
            Assert.AreEqual("Edit ERC overrides", journal.Changes.Single().Description);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "excluded-violation.png"), deadline.Token);
            var stale = new ApplySchematicItemBatch
            {
                Document = first.Document, DocumentEpoch = first.Baseline.DocumentEpoch,
                OperationId = Guid.NewGuid().ToString("D"),
                ExpectedRevision = new() { Epoch = first.Baseline.DocumentEpoch, Sequence = first.Baseline.Sequence }
            };
            stale.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Title = "must not overwrite user intent" } });
            var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                first.Client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, deadline.Token));
            StringAssert.Contains(rejected.Message, "Stale document revision");
            Assert.AreEqual(originalTitle, await first.Client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(
                new() { Document = first.Document }, deadline.Token));
            await first.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = first.Document }, deadline.Token);
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(first.Project, deadline.Token));
            Assert.AreEqual(1, saved.RootElement.GetProperty("erc").GetProperty("erc_exclusions").GetArrayLength());
            var otherJournal = await other.Client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                new() { Document = other.Document }, deadline.Token);
            Assert.AreEqual(other.Baseline.Sequence, otherJournal.Sequence);
            Assert.AreEqual(otherSaveState, await other.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = other.Document }, deadline.Token));
            CollectionAssert.AreEqual(otherProject, await File.ReadAllBytesAsync(other.Project, deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(evidence, "exclusion-result.json"), JsonSerializer.Serialize(new
            {
                instanceId = first.Id, otherInstanceId = other.Id, documentEpoch = journal.DocumentEpoch,
                revision = journal.Sequence, renderedExclusionCommitted = true, staleEditRejected = true,
                savedExclusionCount = 1, otherProjectPreserved = true,
                completeErcOverrideCoverage = false, trackingComplete = false
            }), deadline.Token);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString());
            if (display is not null)
            {
                using var captureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "failure-screen.png"), captureDeadline.Token); }
                catch (Exception captureError)
                { await File.WriteAllTextAsync(Path.Combine(evidence, "capture-failure.txt"), captureError.ToString()); }
            }
            throw;
        }
        finally
        {
            // Every process and file belongs to this disposable fixture; no
            // installed editor or user document is closed by cleanup.
            foreach (var process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(); process.Dispose();
            }
            await Task.WhenAll(logs);
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed record ErcDesign(string Id, NativeClient Client, SchematicChangeJournal Baseline,
        int ProcessId, DocumentSpecifier Document, string Project);

    private static async Task Capture(StreamReader reader, string path)
    {
        await using var output = new StreamWriter(path);
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            await output.WriteAsync(buffer.AsMemory(0, count));
    }
}
