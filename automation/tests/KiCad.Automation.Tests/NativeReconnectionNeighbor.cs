using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private sealed record ReconnectionNeighbor(Process Process, NativeClient Client, string InstanceId,
        string Schematic, DocumentSpecifier Document, string MarkerId, string MarkerText,
        string ProcessEpoch, string EventEpoch, SchematicScreenDataSnapshot Snapshot);

    private static async Task<ReconnectionNeighbor> StartReconnectionNeighbor(ProcessStartInfo original,
        string temporary, string evidence, List<Process> processes, List<Task> captures,
        StdioMcpFixture mcp, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(temporary, "neighbor")).FullName;
        string project = Path.Combine(directory, "neighbor.kicad_pro");
        string schematic = Path.Combine(directory, "neighbor.kicad_sch");
        await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", token);
        string instance = Guid.NewGuid().ToString("D"), socket = Path.Combine(temporary, "neighbor.sock");
        var start = new ProcessStartInfo(original.FileName) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var entry in original.Environment) start.Environment[entry.Key] = entry.Value;
        foreach (string arg in new[] { "--new", "--automation", instance, "--api-socket", socket,
            "--automation-log", Path.Combine(evidence, "neighbor-native.log"), "--software-rendering", project }) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!; processes.Add(process);
        captures.Add(Capture(process.StandardOutput, Path.Combine(evidence, "neighbor.stdout.log")));
        captures.Add(Capture(process.StandardError, Path.Combine(evidence, "neighbor.stderr.log")));
        var client = new NativeClient(new NngTransport(), "ipc://" + socket);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token); startup.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            startup.Token.ThrowIfCancellationRequested(); Assert.IsFalse(process.HasExited);
            try { await client.HandshakeAsync(startup.Token); break; }
            catch (NngException) { }
            catch (NativeApiException error) when (error.Status is 4 or 7) { }
            await Task.Delay(100, startup.Token);
        }
        var created = await client.CreateRootSchematicAsync(schematic, token);
        var before = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = created.Document }, token);
        string markerId = Guid.NewGuid().ToString("D"), markerText = "Independent unsaved neighbor " + Guid.NewGuid().ToString("N");
        var batch = new ApplySchematicItemBatch { Document = created.Document, ExpectedRevision = before.Revision,
            DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Independent neighbor fixture" };
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicText
        { Id = new() { Value = markerId }, Text = new() { Text_ = markerText,
            Position = new() { XNm = 100000000, YNm = 80000000 },
            Attributes = new() { Size = new() { XNm = 2000000, YNm = 2000000 } } } }) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var snapshot = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = created.Document }, token);
        var session = await client.HandshakeAsync(token);
        var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instance });
        RequireToolSuccess(attached);
        return new(process, client, instance, schematic, created.Document, markerId, markerText, session.Epoch, session.EventEpoch, snapshot);
    }

    private static async Task VerifyReconnectionNeighbor(ReconnectionNeighbor neighbor, StdioMcpFixture mcp,
        bool reattach, CancellationToken token)
    {
        Assert.IsFalse(neighbor.Process.HasExited, "Updating another project closed the independent native instance.");
        var session = await neighbor.Client.HandshakeAsync(token);
        Assert.AreEqual(neighbor.ProcessEpoch, session.Epoch); Assert.AreEqual(neighbor.EventEpoch, session.EventEpoch);
        if (reattach) RequireToolSuccess(await mcp.Tool("kicad_instance_reattach", new { instanceId = neighbor.InstanceId }));
        string documentJson = SchematicJson.Formatter.Format(neighbor.Document);
        var data = await mcp.Tool("kicad_schematic_data", new { instanceId = neighbor.InstanceId, documentJson });
        RequireToolSuccess(data);
        var snapshot = SchematicJson.Parser.Parse<SchematicScreenDataSnapshot>(data.GetProperty("structuredContent").GetProperty("snapshot").GetRawText());
        Assert.AreEqual(neighbor.Snapshot.Revision, snapshot.Revision, "The independent design revision changed.");
        var marker = snapshot.Data.Items.Where(item => item.Is(SchematicText.Descriptor))
            .Select(item => item.Unpack<SchematicText>()).Single(item => item.Id.Value == neighbor.MarkerId);
        Assert.AreEqual(neighbor.MarkerText, marker.Text.Text_);
        var saved = await neighbor.Client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = neighbor.Document }, token);
        Assert.IsTrue(saved.UnsavedSchematicChanges); Assert.IsFalse(File.Exists(neighbor.Schematic));
    }

    private static void RequireToolSuccess(JsonElement result) =>
        Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
}
