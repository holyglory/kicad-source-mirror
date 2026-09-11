using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class WindowsIntegratedUpdateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsIntegratedUpdate")]
    public async Task ActualCaptionUpdatePreservesTwoDirtyDesignsAcrossDifferentSignedBuilds()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows packages and desktop."); return; }
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new AssertFailedException("Frozen input required: " + name);
        string baselineCommit = Required("KICAD_WINDOWS_UI_BASELINE_COMMIT"), candidateCommit = Required("KICAD_WINDOWS_UI_EXPECTED_COMMIT");
        foreach (string commit in new[] { baselineCommit, candidateCommit }) Assert.IsTrue(Regex.IsMatch(commit, "^[0-9a-f]{40}$"));
        Assert.AreNotEqual(baselineCommit, candidateCommit, "The actual native build must change.");
        byte[] key = await File.ReadAllBytesAsync(Required("KICAD_WINDOWS_UI_PUBLISHER_SPKI"));
        byte[] baselineEnvelope = await File.ReadAllBytesAsync(Required("KICAD_WINDOWS_UI_BASELINE_ENVELOPE"));
        var baseline = UpdateManifestCodec.Verify(baselineEnvelope, key, "preview");
        Assert.AreEqual(baselineCommit, baseline.Release.Commit);
        var origin = new Uri(Required("KICAD_WINDOWS_UI_ORIGIN"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var downloads = new UpdateDownloader(origin);
        byte[] candidateEnvelope = await downloads.FetchManifestAsync("preview", deadline.Token);
        var candidate = UpdateManifestCodec.Verify(candidateEnvelope, key, "preview", new(baseline.Release.Sequence, baseline.PayloadSha256));
        Assert.AreEqual(candidateCommit, candidate.Release.Commit);
        Assert.IsTrue(candidate.Release.Sequence > baseline.Release.Sequence);
        Assert.IsNotNull(candidate.ForInstallation("win-x64", "zip"));
        string scratch = Directory.CreateTempSubdirectory("kwpair-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-integrated-update")).FullName;
        string root = Path.Combine(scratch, "installed"), state = Path.Combine(scratch, "registry");
        string? priorLibrary = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY");
        var processes = new List<Process>(); var runtimeDirectories = new HashSet<string>();
        using var job = new WindowsProcessJob();
        try
        {
            string archive = (await downloads.DownloadAsync(baseline, "win-x64", "zip",
                Directory.CreateDirectory(Path.Combine(scratch, "download")).FullName, cancellationToken: deadline.Token)).Path;
            var installed = await WindowsVerifiedVersions.InstallAsync(root, archive, baselineEnvelope, key, origin, "preview", deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", Path.Combine(installed.Version.VersionDirectory, "bin/nng.dll"));
            var observer = await WindowsUiObserver.CreateAsync(scratch, evidence, deadline.Token);
            var ui = new WindowsUiAutomation(observer);
            await using var mcp = await WindowsInstalledPackageTests.Mcp.Start(installed.Version.McpExecutable, scratch, state,
                evidence, "pair", job, deadline.Token, traceUpdates: true);
            var first = await Start("first");
            var second = await Start("second");
            await Task.WhenAll(WaitPrepared(first), WaitPrepared(second));
            await ui.WaitButtonAsync(first.Identity, "Update", deadline.Token);
            await ui.WaitButtonAsync(second.Identity, "Update", deadline.Token);
            await Capture(first.Identity, "update-available");
            await ui.PressAsync(first.Identity, "Update", deadline.Token);
            await ui.WaitButtonAsync(first.Identity, "Cancel", deadline.Token);
            await Capture(first.Identity, "cancel-prompt");
            await ui.PressAsync(first.Identity, "Cancel", deadline.Token);
            await ui.WaitButtonAsync(first.Identity, "Update", deadline.Token);
            Assert.AreEqual(installed.SelectionId, WindowsVerifiedVersions.InspectSelectionId(root));
            await Marker(first.Client, first.Document, first.MarkerId, first.MarkerText, dirty: true);
            Assert.IsFalse(File.Exists(first.Schematic));
            var updatedFirst = await Update(first, "first");
            Assert.AreEqual(second.Identity, WindowsProcessIdentity.Read(second.Identity.ProcessId));
            Assert.AreEqual(second.Client.Epoch, (await second.Client.HandshakeAsync(deadline.Token)).Epoch);
            await Marker(second.Client, second.Document, second.MarkerId, second.MarkerText, dirty: true);
            Assert.IsFalse(File.Exists(second.Schematic));
            var updatedSecond = await Update(second, "second");
            CollectionAssert.AreEqual(candidateEnvelope, await downloads.FetchManifestAsync("preview", deadline.Token), "The frozen public feed changed.");
            foreach (var process in new[] { updatedFirst, updatedSecond })
            {
                nint schematic = await WindowsNativeUi.WaitForWindow(process, "Schematic Editor", deadline.Token);
                WindowsNativeUi.Close(process, schematic);
                await WindowsNativeUi.WaitForClosedWindow(process, schematic, deadline.Token);
                WindowsNativeUi.Close(process, await WindowsNativeUi.WaitForWindow(process, "KiCad", deadline.Token));
                await process.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, process.ExitCode);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, baselineCommit, candidateCommit, platform = "win-x64", publicSignedFeed = origin.AbsoluteUri,
                actualCaptionClicks = true, automaticInstalledContext = true, cancelPreservedDirtyObject = true,
                savePersistedMarkerAndIdentity = true, secondInstancePreserved = true, bothInstancesRestarted = true,
                nativeEpochsChanged = true, postUpdateMcpReconnectionVerified = false,
                automaticUpdatingQualified = false, crossPlatformReady = false
            }), deadline.Token);

            async Task<Design> Start(string name)
            {
                string directory = Directory.CreateDirectory(Path.Combine(scratch, name)).FullName;
                string project = Path.Combine(directory, name + ".kicad_pro"), schematic = Path.ChangeExtension(project, ".kicad_sch");
                await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
                var started = await mcp.Tool("kicad_instance_start", new { executable = installed.Version.NativeExecutable,
                    projectPath = project, softwareRendering = true });
                string id = started.GetProperty("structuredContent").GetProperty("instanceId").GetString()!;
                var record = JsonSerializer.Deserialize<InstanceRecord>(await File.ReadAllTextAsync(Path.Combine(state, id + ".json"), deadline.Token))!;
                Assert.AreEqual(project, record.ProjectPath); Assert.IsNotNull(record.ProcessId);
                var process = Process.GetProcessById(record.ProcessId.Value);
                if (!job.Contains(process)) { process.Dispose(); Assert.Fail("Only this fixture's child may receive input."); }
                processes.Add(process); runtimeDirectories.Add(NativeIpcEndpoint.RuntimeDirectory(id));
                var client = new NativeClient(new NngTransport(), record.Endpoint, record.Epoch);
                var session = await client.HandshakeAsync(deadline.Token); Assert.AreEqual(id, session.InstanceId);
                var created = await mcp.Tool("kicad_schematic_create", new { instanceId = id, path = schematic });
                string documentJson = created.GetProperty("content").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
                string markerId = Guid.NewGuid().ToString("D"), markerText = "Unsaved Windows " + name + " design " + Guid.NewGuid().ToString("N");
                var before = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = before.Revision,
                    DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Update qualification fixture" };
                batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicText
                {
                    Id = new() { Value = markerId }, Text = new() { Text_ = markerText,
                        Position = new() { XNm = 100000000, YNm = 80000000 },
                        Attributes = new() { Size = new() { XNm = 2000000, YNm = 2000000 } } }
                }) });
                await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, deadline.Token);
                await Marker(client, document, markerId, markerText, dirty: true);
                return new(record, WindowsProcessIdentity.Read(process.Id), client, document, schematic, markerId, markerText);
            }

            async Task WaitPrepared(Design design)
            {
                string directory = NativeIpcEndpoint.RuntimeDirectory(design.Record.InstanceId), path = Path.Combine(directory, "native.log");
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); ready.CancelAfter(TimeSpan.FromMinutes(5));
                using var changed = new SemaphoreSlim(0, 1);
                using var watcher = new FileSystemWatcher(directory, "native.log") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += (_, _) => { try { changed.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } };
                watcher.EnableRaisingEvents = true;
                while (true)
                {
                    Assert.AreEqual(design.Identity, WindowsProcessIdentity.Read(design.Identity.ProcessId));
                    string log = File.Exists(path) ? await File.ReadAllTextAsync(path, ready.Token) : "";
                    if (log.Contains("\"status\":\"candidate_available\"", StringComparison.Ordinal)) return;
                    Assert.IsFalse(log.Contains("\"status\":\"failed\"", StringComparison.Ordinal), "Native update preparation failed; see retained native.log.");
                    await changed.WaitAsync(TimeSpan.FromSeconds(2), ready.Token);
                }
            }

            async Task<Process> Update(Design old, string name)
            {
                string handovers = Path.Combine(root, "handovers");
                string[] before = Directory.Exists(handovers) ? Directory.GetDirectories(handovers) : [];
                await ui.WaitButtonAsync(old.Identity, "Update", deadline.Token);
                await ui.PressAsync(old.Identity, "Update", deadline.Token);
                await ui.WaitButtonAsync(old.Identity, "Save", deadline.Token); await Capture(old.Identity, name + "-save-prompt");
                await ui.PressAsync(old.Identity, "Save", deadline.Token);
                var original = processes.Single(x => x.Id == old.Identity.ProcessId);
                await original.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, original.ExitCode);
                byte[] saved = await File.ReadAllBytesAsync(old.Schematic, deadline.Token);
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); ready.CancelAfter(TimeSpan.FromMinutes(3));
                WindowsUpdateHandoffState? result = null; int delay = 50;
                while (result is null)
                {
                    foreach (string journal in Directory.Exists(handovers) ? Directory.GetDirectories(handovers).Except(before) : [])
                    {
                        string path = Path.Combine(journal, "state.json"); if (!File.Exists(path)) continue;
                        var observed = JsonSerializer.Deserialize<WindowsUpdateHandoffState>(await File.ReadAllBytesAsync(path, ready.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                        Assert.IsFalse(observed.Status is "activation_failed" or "reconciliation_required" or "launch_failed", observed.Error);
                        if (observed.Status == "restarted") result = observed;
                    }
                    if (result is null) { await Task.Delay(delay, ready.Token); delay = Math.Min(delay * 2, 1000); }
                }
                Assert.IsNotNull(result.ProcessId); Assert.IsNotNull(result.ProcessIdentity); Assert.IsNotNull(result.Endpoint);
                var process = Process.GetProcessById(result.ProcessId.Value);
                if (!job.Contains(process)) { process.Dispose(); Assert.Fail("The replacement escaped the fixture's owned process job."); }
                processes.Add(process); Assert.AreEqual(result.ProcessIdentity, WindowsProcessIdentity.Read(process.Id));
                var version = await WindowsVerifiedVersions.InspectExecutableAsync(root, result.ProcessIdentity.Executable, ready.Token);
                Assert.AreEqual(candidateCommit, version.Commit);
                var client = new NativeClient(new NngTransport(), result.Endpoint, result.NativeEpoch);
                var session = await client.HandshakeAsync(ready.Token);
                Assert.AreEqual(old.Record.InstanceId, session.InstanceId); Assert.AreEqual(old.Record.ProjectPath, session.ProjectPath);
                Assert.AreNotEqual(old.Record.Epoch, session.Epoch);
                var document = (await client.OpenRootSchematicAsync(old.Schematic, ready.Token)).Document;
                await Marker(client, document, old.MarkerId, old.MarkerText, dirty: false);
                CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(old.Schematic, ready.Token));
                await Capture(result.ProcessIdentity, name + "-restarted");
                return process;
            }

            async Task Marker(NativeClient client, DocumentSpecifier document, string id, string text, bool dirty)
            {
                var snapshot = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                var marker = snapshot.Data.Items.Where(x => x.Is(SchematicText.Descriptor)).Select(x => x.Unpack<SchematicText>()).Single(x => x.Id.Value == id);
                Assert.AreEqual(text, marker.Text.Text_);
                var saved = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, deadline.Token);
                Assert.AreEqual(dirty, saved.UnsavedSchematicChanges);
            }

            async Task Capture(WindowsProcessIdentity identity, string name)
            {
                var observed = await observer.InspectAsync(identity, deadline.Token);
                using var process = Process.GetProcessById(identity.ProcessId); Assert.IsTrue(job.Contains(process));
                var windows = observed.GetProperty("windows").EnumerateArray().ToArray(); Assert.IsTrue(windows.Length > 0);
                int index = 0;
                foreach (var window in windows.Take(5))
                    WindowsNativeUi.Capture(process, checked((nint)ulong.Parse(window.GetProperty("handle").GetString()!, CultureInfo.InvariantCulture)),
                        Path.Combine(evidence, name + "-" + index++ + ".png"));
                await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), observed.GetRawText(), deadline.Token);
            }
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString()); throw; }
        finally
        {
            job.Dispose();
            foreach (var process in processes)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(cleanup.Token); process.Dispose();
            }
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", priorLibrary);
            // Include unacknowledged starts recorded before a failed MCP reply.
            foreach (string file in Directory.Exists(state) ? Directory.GetFiles(state, "*.json", SearchOption.AllDirectories) : [])
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id))
                    runtimeDirectories.Add(NativeIpcEndpoint.RuntimeDirectory(id.ToString("D")));
            foreach (string directory in runtimeDirectories.Where(Directory.Exists))
            {
                string destination = Directory.CreateDirectory(Path.Combine(evidence, Path.GetFileName(directory))).FullName;
                foreach (string file in Directory.GetFiles(directory, "*.log")) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                Directory.Delete(directory, recursive: true); // Exact recorded runtime directory; all owned processes have exited.
            }
            string handovers = Path.Combine(root, "handovers");
            foreach (string file in Directory.Exists(handovers) ? Directory.GetFiles(handovers, "*.json", SearchOption.AllDirectories) : [])
            {
                string destination = Path.Combine(evidence, "handovers", Path.GetRelativePath(handovers, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
        }
    }

    private sealed record Design(InstanceRecord Record, WindowsProcessIdentity Identity, NativeClient Client,
        DocumentSpecifier Document, string Schematic, string MarkerId, string MarkerText);
}
