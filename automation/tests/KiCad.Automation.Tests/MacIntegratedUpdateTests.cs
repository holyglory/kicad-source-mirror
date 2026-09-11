using System.Diagnostics;
using System.Runtime.InteropServices;
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

[TestClass]
[DoNotParallelize]
public sealed class MacIntegratedUpdateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("ExternalIntegration")]
    [TestCategory("NativeMacIntegratedUpdate")]
    public async Task ActualCaptionUpdatePreservesDirtyObjectsAndIndependentProjects()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Native Mac Update-button journey."); return; }
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new AssertFailedException("The integrated journey requires explicit frozen input: " + name);
        string? archive = Environment.GetEnvironmentVariable("KICAD_MAC_UI_BASELINE_ARCHIVE");
        if (archive is null && Environment.GetEnvironmentVariable("KICAD_MAC_UI_DOWNLOAD_BASELINE") != "true")
            throw new AssertFailedException("Supply an explicit baseline archive or opt into its authenticated download.");
        string baselineEnvelopePath = Required("KICAD_MAC_UI_BASELINE_ENVELOPE");
        string keyPath = Required("KICAD_MAC_UI_PUBLISHER_SPKI");
        string expectedBaselineCommit = Required("KICAD_MAC_UI_BASELINE_COMMIT");
        string expectedCommit = Required("KICAD_MAC_UI_EXPECTED_COMMIT");
        MacUpdatePair.Validate(expectedBaselineCommit, expectedCommit);
        var origin = new Uri(Required("KICAD_MAC_UI_ORIGIN"));
        string platform = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        byte[] key = await File.ReadAllBytesAsync(keyPath), baselineEnvelope = await File.ReadAllBytesAsync(baselineEnvelopePath);
        var baseline = UpdateManifestCodec.Verify(baselineEnvelope, key, "preview");
        Assert.AreEqual(expectedBaselineCommit, baseline.Release.Commit);
        using var downloads = new UpdateDownloader(origin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        byte[] candidateEnvelope = await downloads.FetchManifestAsync("preview", deadline.Token);
        var candidate = UpdateManifestCodec.Verify(candidateEnvelope, key, "preview", new(baseline.Release.Sequence, baseline.PayloadSha256));
        Assert.AreEqual(expectedCommit, candidate.Release.Commit);
        Assert.IsTrue(candidate.Release.Sequence > baseline.Release.Sequence);
        Assert.IsNotNull(candidate.ForInstallation(platform, "tar.gz"));
        Assert.AreNotEqual(baseline.Release.Commit, candidate.Release.Commit, "This journey must change the actual source build.");
        string scratch = Directory.CreateTempSubdirectory("kicad-mac-caption-journey-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "mac-integrated-update")).FullName;
        string installation = Path.Combine(scratch, "installed");
        var processes = new List<Process>();
        var captures = new List<Task>();
        string? previousNng = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY");
        try
        {
            var ui = await MacUiAutomation.CreateAsync(scratch, evidence, deadline.Token);
            if (archive is null)
            {
                string downloadRoot = Directory.CreateDirectory(Path.Combine(scratch, "baseline-download")).FullName;
                archive = (await downloads.DownloadAsync(baseline, platform, "tar.gz", downloadRoot, cancellationToken: deadline.Token)).Path;
            }
            string quit = await MacProcessIdentityTests.CompileProbe(scratch, deadline.Token);
            var installed = await MacVerifiedInstallation.InstallAsync(installation, archive, baselineEnvelope, key, origin, "preview", deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", Path.Combine(installed.VersionDirectory, "managed/libnng.dylib"));
            string initialTarget = MacVerifiedInstallation.InspectTarget(installation);
            var first = await Start("first");
            var second = await Start("second");
            string secondEpoch = second.Client.Epoch;
            await Task.WhenAll(WaitPrepared(first, "first"), WaitPrepared(second, "second"));
            await ui.WaitButtonAsync(first.Identity, "Update", deadline.Token);
            await ui.WaitButtonAsync(second.Identity, "Update", deadline.Token);
            Assert.IsFalse(File.Exists(first.Schematic));
            await ui.CaptureAsync(first.Identity, "update-available", deadline.Token);

            await ui.PressAsync(first.Identity, "Update", deadline.Token);
            await ui.WaitButtonAsync(first.Identity, "Cancel", deadline.Token);
            await ui.CaptureAsync(first.Identity, "cancel-prompt", deadline.Token);
            await ui.PressAsync(first.Identity, "Cancel", deadline.Token);
            await ui.WaitButtonAsync(first.Identity, "Update", deadline.Token);
            Assert.IsFalse(first.Process.HasExited);
            Assert.AreEqual(initialTarget, MacVerifiedInstallation.InspectTarget(installation));
            Assert.IsFalse(File.Exists(first.Schematic));
            await VerifyMarker(first.Client, first.Document, first.MarkerId, first.MarkerText);
            Assert.IsTrue((await first.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = first.Document }, deadline.Token)).UnsavedSchematicChanges);

            var updatedFirst = await UpdateAndSave(first, "first");
            Assert.IsFalse(second.Process.HasExited, "Updating one design closed the other instance.");
            Assert.AreEqual(secondEpoch, (await second.Client.HandshakeAsync(deadline.Token)).Epoch);
            Assert.IsFalse(File.Exists(second.Schematic));
            await VerifyMarker(second.Client, second.Document, second.MarkerId, second.MarkerText);
            var updatedSecond = await UpdateAndSave(second, "second");
            CollectionAssert.AreEqual(candidateEnvelope, await downloads.FetchManifestAsync("preview", deadline.Token),
                "The live feed changed during a frozen qualification journey.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, platform, baselineCommit = baseline.Release.Commit, candidateCommit = candidate.Release.Commit,
                automaticInstalledContext = true, actualAccessibilityPress = true, cancelPreservedDirtyObject = true,
                savePersistedMarkerAndIdentity = true, secondInstancePreserved = true, bothInstancesRestarted = true,
                publicSignedFeed = origin.AbsoluteUri, nativeEpochsChanged = true,
                automaticUpdatingQualified = false, crossPlatformReady = false
            }), deadline.Token);
            foreach (var process in new[] { updatedFirst, updatedSecond })
            {
                await MacProcessIdentityTests.Run(quit, ["quit", process.Id.ToString()], deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
            }

            async Task<OpenProject> Start(string name)
            {
                string directory = Directory.CreateDirectory(Path.Combine(scratch, name)).FullName;
                string project = Path.Combine(directory, name + ".kicad_pro"), schematic = Path.Combine(directory, name + ".kicad_sch");
                await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
                {
                    meta = new { version = 3 }, schematic = new { top_level_sheets = new[]
                    { new { uuid = Guid.NewGuid().ToString("D"), name, filename = name + ".kicad_sch" } } }
                }), deadline.Token);
                Guid instance = Guid.NewGuid();
                string socket = "/tmp/kmu-" + Guid.NewGuid().ToString("N")[..12] + ".sock";
                var start = new ProcessStartInfo(installed.NativeExecutable) { UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
                start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(scratch, "profile");
                start.Environment["KICAD_CACHE_HOME"] = Path.Combine(scratch, "cache");
                start.Environment["WXTRACE"] = "KICAD_AUTOMATION_UPDATES";
                start.Environment.Remove("KICAD_AUTOMATION_UPDATE_HELPER");
                start.Environment.Remove("KICAD_AUTOMATION_UPDATE_CONFIG");
                start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
                foreach (string argument in new[] { "--new", "--automation", instance.ToString("D"), "--api-socket", socket,
                    "--automation-log", Path.Combine(evidence, name + "-native.log"), project }) start.ArgumentList.Add(argument);
                var process = Process.Start(start)!; processes.Add(process);
                captures.Add(Capture(process.StandardOutput.BaseStream, name + ".stdout.log"));
                captures.Add(Capture(process.StandardError.BaseStream, name + ".stderr.log"));
                var client = new NativeClient(new NngTransport(), "ipc://" + socket);
                await Ready(client, process);
                var document = (await client.CreateRootSchematicAsync(schematic, deadline.Token)).Document;
                string markerId = Guid.NewGuid().ToString("D"), markerText = "Unsaved " + name + " engineering note " + Guid.NewGuid().ToString("N");
                var snapshot = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                var batch = new ApplySchematicItemBatch { Document = document, Description = "Native update preservation fixture",
                    ExpectedRevision = snapshot.Revision, DocumentEpoch = snapshot.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
                batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicText
                {
                    Id = new() { Value = markerId }, Text = new() { Text_ = markerText,
                        Position = new() { XNm = 100000000, YNm = 80000000 },
                        Attributes = new() { Size = new() { XNm = 2000000, YNm = 2000000 } } }
                }) });
                await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, deadline.Token);
                return new(process, MacProcessIdentity.Read(process.Id), client, document, project, schematic, instance, markerId, markerText);
            }

            async Task WaitPrepared(OpenProject project, string name)
            {
                string path = Path.Combine(evidence, name + "-native.log");
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                ready.CancelAfter(TimeSpan.FromMinutes(5));
                using var changed = new SemaphoreSlim(0, 1);
                using var watcher = new FileSystemWatcher(evidence, name + "-native.log")
                { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                FileSystemEventHandler wake = (_, _) =>
                {
                    try { changed.Release(); }
                    catch (SemaphoreFullException) { }
                    catch (ObjectDisposedException) { } // A queued filesystem event may outlive watcher disposal.
                };
                watcher.Changed += wake; watcher.Created += wake; watcher.EnableRaisingEvents = true;
                while (true)
                {
                    ready.Token.ThrowIfCancellationRequested();
                    Assert.IsFalse(project.Process.HasExited, "The native instance exited during update preparation.");
                    string log = File.Exists(path) ? await File.ReadAllTextAsync(path, ready.Token) : "";
                    if (log.Contains("\"status\":\"candidate_available\"", StringComparison.Ordinal)) return;
                    Assert.IsFalse(log.Contains("\"status\":\"failed\"", StringComparison.Ordinal), "Native update preparation failed; see " + name + "-native.log.");
                    // Filesystem notifications drive readiness; a bounded fallback
                    // recovers a missed notification without polling the design.
                    await changed.WaitAsync(TimeSpan.FromSeconds(2), ready.Token);
                }
            }

            async Task<Process> UpdateAndSave(OpenProject old, string name)
            {
                string handovers = Path.Combine(installation, "handovers");
                string[] before = Directory.Exists(handovers) ? Directory.GetDirectories(handovers) : [];
                await ui.WaitButtonAsync(old.Identity, "Update", deadline.Token);
                await ui.PressAsync(old.Identity, "Update", deadline.Token);
                await ui.WaitButtonAsync(old.Identity, "Save", deadline.Token);
                await ui.CaptureAsync(old.Identity, name + "-save-prompt", deadline.Token);
                await ui.PressAsync(old.Identity, "Save", deadline.Token);
                await old.Process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, old.Process.ExitCode);
                Assert.IsTrue(File.Exists(old.Schematic));
                byte[] saved = await File.ReadAllBytesAsync(old.Schematic, deadline.Token);
                MacUpdateHandoffState? result = null;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                wait.CancelAfter(TimeSpan.FromMinutes(3));
                while (result is null)
                {
                    foreach (string journal in Directory.Exists(handovers) ? Directory.GetDirectories(handovers).Except(before) : [])
                    {
                        string path = Path.Combine(journal, "state.json");
                        if (!File.Exists(path)) continue;
                        var state = JsonSerializer.Deserialize<MacUpdateHandoffState>(await File.ReadAllBytesAsync(path, wait.Token),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                        Assert.IsFalse(state.Status is "activation_failed" or "reconciliation_required" or "launch_failed", state.Error);
                        if (state.Status == "restarted") result = state;
                    }
                    if (result is null) await Task.Delay(100, wait.Token);
                }
                var replacement = Process.GetProcessById(result.ProcessId!.Value); processes.Add(replacement);
                Assert.AreEqual(result.ProcessIdentity, MacProcessIdentity.Read(replacement.Id));
                var version = await MacVerifiedInstallation.InspectExecutableVersionAsync(installation, result.ProcessIdentity!.Executable, wait.Token);
                Assert.AreEqual(candidate.Release.Commit, version.Commit);
                var client = new NativeClient(new NngTransport(), result.Endpoint!, result.NativeEpoch);
                var session = await client.HandshakeAsync(wait.Token);
                Assert.AreEqual(old.Instance.ToString("D"), session.InstanceId);
                Assert.AreNotEqual(old.Client.Epoch, session.Epoch);
                Assert.AreEqual(old.Project, session.ProjectPath);
                var document = (await client.OpenRootSchematicAsync(old.Schematic, wait.Token)).Document;
                await VerifyMarker(client, document, old.MarkerId, old.MarkerText);
                CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(old.Schematic, wait.Token));
                await ui.CaptureAsync(result.ProcessIdentity, name + "-restarted", wait.Token);
                return replacement;
            }

            async Task VerifyMarker(NativeClient client, DocumentSpecifier document, string id, string text)
            {
                var snapshot = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                var notes = snapshot.Data.Items.Where(x => x.Is(SchematicText.Descriptor)).Select(x => x.Unpack<SchematicText>()).ToArray();
                Assert.IsTrue(notes.Any(x => x.Id.Value == id && x.Text.Text_ == text), "The unsaved design object or stable identity was lost.");
            }
            async Task Ready(NativeClient client, Process process)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); wait.CancelAfter(TimeSpan.FromSeconds(60));
                while (true)
                {
                    Assert.IsFalse(process.HasExited);
                    try { await client.HandshakeAsync(wait.Token); return; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(100, wait.Token);
                }
            }
            async Task Capture(Stream stream, string name)
            {
                await using var file = File.Create(Path.Combine(evidence, name));
                await stream.CopyToAsync(file);
            }
        }
        finally
        {
            deadline.Cancel();
            // Stop only helpers identified in this fixture's native logs and
            // reverified inside its unique installation. Do this before closing
            // old editors so a failed test cannot trigger a late replacement.
            if (Directory.Exists(installation))
            {
                string owned = MacProcessIdentity.PhysicalExecutable(installation) + "/versions/";
                foreach (string log in Directory.GetFiles(evidence, "*-native.log"))
                    foreach (Match match in Regex.Matches(await File.ReadAllTextAsync(log), "\\\"helperPid\\\":([0-9]+)"))
                    {
                        if (!int.TryParse(match.Groups[1].Value, out int pid)) continue;
                        try
                        {
                            var identity = MacProcessIdentity.Read(pid);
                            if (!identity.Executable.StartsWith(owned, StringComparison.Ordinal)
                                || !identity.Executable.EndsWith("/payload/managed/kicad-mcp", StringComparison.Ordinal)) continue;
                            using var helper = Process.GetProcessById(pid);
                            if (!helper.HasExited && MacProcessIdentity.Read(pid) == identity)
                            { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
                        }
                        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException) { }
                    }
                string handovers = Path.Combine(installation, "handovers");
                if (Directory.Exists(handovers))
                    foreach (string statePath in Directory.GetFiles(handovers, "state.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var state = JsonSerializer.Deserialize<MacUpdateHandoffState>(await File.ReadAllBytesAsync(statePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                            if (state?.ProcessIdentity is not { } identity || !identity.Executable.StartsWith(owned, StringComparison.Ordinal)) continue;
                            if (MacProcessIdentity.Read(identity.ProcessId) != identity) continue;
                            using var orphan = Process.GetProcessById(identity.ProcessId);
                            if (!orphan.HasExited) { orphan.Kill(entireProcessTree: true); await orphan.WaitForExitAsync(); }
                        }
                        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or JsonException or InvalidOperationException) { }
                    }
            }
            foreach (var process in processes)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            await Task.WhenAll(captures);
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", previousNng);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed record OpenProject(Process Process, MacProcessIdentity Identity, NativeClient Client, DocumentSpecifier Document,
        string Project, string Schematic, Guid Instance, string MarkerId, string MarkerText);
}
