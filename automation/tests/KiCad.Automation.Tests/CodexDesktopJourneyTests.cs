using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class CodexDesktopJourneyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeCodexDesktop")]
    public async Task HoldTwoRealEditorsForActualCodexToolOperation()
    {
        Assert.IsTrue(OperatingSystem.IsLinux(), "This fixture proves the VPS execution side, not a native Mac host.");
        string package = Environment.GetEnvironmentVariable("KICAD_DESKTOP_PACKAGE_ROOT")
            ?? throw new AssertFailedException("Select an exact verified native package.");
        string control = Environment.GetEnvironmentVariable("KICAD_DESKTOP_CONTROL_ROOT")
            ?? throw new AssertFailedException("Select a new isolated operator-control directory.");
        Assert.IsTrue(Path.IsPathFullyQualified(control) && !Directory.Exists(control));
        Directory.CreateDirectory(control);
        string requests = Directory.CreateDirectory(Path.Combine(control, "requests")).FullName;
        string responses = Directory.CreateDirectory(Path.Combine(control, "responses")).FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(control, "evidence")).FullName;
        string scratch = Directory.CreateTempSubdirectory("kicad-desktop-fixture-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var processes = new List<Process>(); var captures = new List<Task>(); var designs = new List<Design>();
        try
        {
            var displayStart = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" }) displayStart.ArgumentList.Add(arg);
            var displayProcess = Process.Start(displayStart)!; processes.Add(displayProcess);
            captures.Add(Capture(displayProcess.StandardError.BaseStream, "xvfb.stderr.log"));
            string? number = await displayProcess.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.IsTrue(int.TryParse(number, out _)); string display = ":" + number;
            for (int index = 0; index < 2; index++)
            {
                string directory = Directory.CreateDirectory(Path.Combine(scratch, "project-" + index)).FullName;
                string project = Path.Combine(directory, "fixture.kicad_pro"), schematic = Path.Combine(directory, "fixture.kicad_sch");
                string root = Guid.NewGuid().ToString("D"), id = Guid.NewGuid().ToString("D");
                var electrical = NativeSessionTests.MakeElectricalFixture(root);
                await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new { meta = new { version = 3 }, schematic = new
                { top_level_sheets = new[] { new { uuid = root, name = "fixture", filename = "fixture.kicad_sch" } } } }), deadline.Token);
                await File.WriteAllTextAsync(schematic, $"(kicad_sch (version 20250114) (generator eeschema) (uuid {root}) (paper \"A4\") "
                    + electrical.Contents + " (sheet_instances (path \"/\" (page \"1\"))))", deadline.Token);
                string socket = Path.Combine(directory, "api.sock");
                var start = new ProcessStartInfo(Path.Combine(package, "kicad-codex"))
                { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["DISPLAY"] = display;
                start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(directory, "profile");
                start.Environment["KICAD_CACHE_HOME"] = Path.Combine(directory, "cache");
                start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
                foreach (string arg in new[] { "--new", "--automation", id, "--api-socket", socket, "--automation-log",
                    Path.Combine(evidence, "native-" + index + ".log"), "--software-rendering", project }) start.ArgumentList.Add(arg);
                var process = Process.Start(start)!; processes.Add(process);
                captures.Add(Capture(process.StandardOutput.BaseStream, "native-" + index + ".stdout.log"));
                captures.Add(Capture(process.StandardError.BaseStream, "native-" + index + ".stderr.log"));
                var native = new NativeClient(new NngTransport(), NativeIpcEndpoint.FromSocketPath(socket));
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); ready.CancelAfter(TimeSpan.FromSeconds(60));
                while (true)
                {
                    Assert.IsFalse(process.HasExited);
                    try { await native.HandshakeAsync(ready.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(100, ready.Token);
                }
                var document = (await native.OpenRootSchematicAsync(schematic, ready.Token)).Document;
                var baseline = await Snapshot(native, document);
                designs.Add(new(id, process, native, document, electrical, baseline));
            }
            await File.WriteAllTextAsync(Path.Combine(control, "ready.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, display, package, operatingSystem = "Linux", externalOperatorRequired = true,
                designs = designs.Select(d => new { instanceId = d.Id, endpoint = d.Native.Endpoint, processId = d.Process.Id,
                    processEpoch = d.Native.Epoch, documentJson = SchematicJson.Formatter.Format(d.Document), symbolId = d.Fixture.Symbol,
                    pinA = d.Fixture.PinA, pinB = d.Fixture.PinB, documentEpoch = d.Baseline.Revision.Epoch,
                    expectedRevision = d.Baseline.Revision.Sequence, moveOperationId = d.MoveOperation, staleOperationId = d.StaleOperation,
                    deltaXNm = 2540000L, deltaYNm = 2540000L })
            }), deadline.Token);
            TestContext.WriteLine("Actual-client fixtures ready: " + Path.Combine(control, "ready.json"));
            using var changed = new SemaphoreSlim(0, 1);
            using var watcher = new FileSystemWatcher(requests, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler wake = (_, _) => { try { changed.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } };
            watcher.Created += wake; watcher.Changed += wake; watcher.EnableRaisingEvents = true;
            var handled = new HashSet<string>(); bool finish = false;
            while (!finish)
            {
                deadline.Token.ThrowIfCancellationRequested();
                foreach (string path in Directory.GetFiles(requests, "*.json").Order())
                {
                    if (handled.Contains(path)) continue;
                    Control? request;
                    try { request = JsonSerializer.Deserialize<Control>(await File.ReadAllTextAsync(path, deadline.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                    catch (Exception error) when (error is IOException or JsonException) { continue; }
                    Assert.IsNotNull(request); Assert.IsTrue(Guid.TryParseExact(request.RequestId, "D", out _));
                    if (request.Action == "finish")
                    {
                        Assert.IsTrue(designs.All(d => d.Stage == "undone")); finish = true;
                    }
                    else
                    {
                        var design = designs.Single(d => d.Id == request.InstanceId);
                        var before = await Snapshot(design.Native, design.Document);
                        if (request.Action == "verify_move")
                        {
                            Assert.AreEqual("ready", design.Stage);
                            var original = Symbol(design.Baseline, design.Fixture.Symbol); var moved = Symbol(before, design.Fixture.Symbol);
                            Assert.AreEqual(original.Position.XNm + 2540000, moved.Position.XNm);
                            Assert.AreEqual(original.Position.YNm + 2540000, moved.Position.YNm);
                            await Connectivity(design); design.Stage = "moved";
                        }
                        else if (request.Action == "undo")
                        {
                            Assert.AreEqual("moved", design.Stage);
                            await NativeSessionTests.FocusedSchematicShortcut(design.Native, design.Document, design.Process.Id, display, "z", deadline.Token);
                            using var undo = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); undo.CancelAfter(TimeSpan.FromSeconds(10));
                            while ((await Snapshot(design.Native, design.Document)).Revision.Sequence == before.Revision.Sequence)
                                await Task.Delay(100, undo.Token);
                            Assert.AreEqual(design.Baseline.Data, (await Snapshot(design.Native, design.Document)).Data);
                            await Connectivity(design); design.Stage = "undone";
                        }
                        else Assert.Fail("Unknown operator action.");
                        foreach (var other in designs.Where(d => d.Id != design.Id))
                        {
                            Assert.IsFalse(other.Process.HasExited);
                            Assert.AreEqual(other.Baseline.Data, (await Snapshot(other.Native, other.Document)).Data);
                        }
                        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, design.Id + "-" + request.Action + ".png"), deadline.Token);
                    }
                    handled.Add(path);
                    await File.WriteAllTextAsync(Path.Combine(responses, request.RequestId + ".json"), JsonSerializer.Serialize(new
                    { schemaVersion = 1, request.RequestId, request.Action, status = "verified", stages = designs.Select(d => new { d.Id, d.Stage }) }), deadline.Token);
                }
                if (!finish) await changed.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, nativeConnectedMovesAndUndoVerified = true, twoProjectsPreserved = true,
                externalToolTranscriptRequired = true, nativeMacVerified = false, completeDesktopQualification = false }), deadline.Token);
        }
        finally
        {
            foreach (var process in processes.AsEnumerable().Reverse())
            { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); }
            await Task.WhenAll(captures).WaitAsync(TimeSpan.FromSeconds(15));
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            Directory.Delete(scratch, true);
        }

        async Task Capture(Stream stream, string name)
        { await using var file = File.Create(Path.Combine(evidence, name)); await stream.CopyToAsync(file); }
        Task<SchematicScreenDataSnapshot> Snapshot(NativeClient client, DocumentSpecifier document) =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
        static SchematicSymbolInstance Symbol(SchematicScreenDataSnapshot data, string id) => data.Data.Items
            .Where(x => x.Is(SchematicSymbolInstance.Descriptor)).Select(x => x.Unpack<SchematicSymbolInstance>()).Single(x => x.Id.Value == id);
        async Task Connectivity(Design design)
        {
            var nets = await design.Native.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = design.Document }, deadline.Token);
            var a = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(x => x.Value == design.Fixture.PinA));
            var b = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(x => x.Value == design.Fixture.PinB));
            Assert.AreEqual(a.Name, b.Name);
        }
    }

    private sealed record Control(string RequestId, string Action, string? InstanceId);
    private sealed class Design(string id, Process process, NativeClient native, DocumentSpecifier document,
        NativeSessionTests.ElectricalFixture fixture, SchematicScreenDataSnapshot baseline)
    {
        public string Id { get; } = id;
        public Process Process { get; } = process;
        public NativeClient Native { get; } = native;
        public DocumentSpecifier Document { get; } = document;
        public NativeSessionTests.ElectricalFixture Fixture { get; } = fixture;
        public SchematicScreenDataSnapshot Baseline { get; } = baseline;
        public string MoveOperation { get; } = Guid.NewGuid().ToString("D");
        public string StaleOperation { get; } = Guid.NewGuid().ToString("D");
        public string Stage { get; set; } = "ready";
    }
}
