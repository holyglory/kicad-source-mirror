using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyElectricalState(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, string evidence, string instanceId, CancellationToken token)
    {
        var request = new ReadSchematicElectricalState { Document = document };
        var before = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        var view = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        var state = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token);
        Assert.AreEqual(before.Revision, state.Hierarchy.Revision);
        Assert.AreEqual(document, state.Hierarchy.Data.Document);
        Assert.IsFalse(state.Hierarchy.TrackingComplete);
        var net = state.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinA));
        Assert.IsTrue(net.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinB));
        Assert.IsTrue(net.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.Wire));
        request.ExpectedRevision = state.Hierarchy.Revision.Clone();
        Assert.AreEqual(state, await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token));
        var stale = request.Clone(); stale.ExpectedRevision.Sequence++;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(stale, token))).Status);
        var wrong = request.Clone(); wrong.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(wrong, token));
        var future = request.Clone(); future.SchemaVersion = 99;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(future, token))).Status);

        var commit = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = new() { Document = document } }, token);
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token))).Status);
        await client.InvokeAsync<EndCommit, EndCommitResponse>(new() { Header = new() { Document = document }, Id = commit.Id, Action = (CommitAction)2 }, token);
        Assert.AreEqual(state, await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token));
        Assert.AreEqual(before, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token));
        var afterView = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        Assert.AreEqual(view.Viewport, afterView.Viewport);
        Assert.AreEqual(view.Document, afterView.Document);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-electrical-state.json"), SchematicJson.Formatter.Format(state), token);

        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-electrical-mcp"),
            Path.Combine(evidence, instanceId + "-electrical-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var result = await mcp.Tool("kicad_schematic_electrical_state", new { instanceId, requestJson = SchematicJson.Formatter.Format(request) });
        RequireToolSuccess(result);
        var actual = SchematicJson.Parser.Parse<SchematicElectricalState>(result.GetProperty("structuredContent").GetProperty("state").GetRawText());
        Assert.AreEqual(state, actual);
        Assert.AreEqual(SchematicDataXml.Write(state.Hierarchy.Data), result.GetProperty("structuredContent").GetProperty("xml").GetString());
        Assert.IsTrue((await mcp.Tool("kicad_schematic_electrical_state", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(stale) })).GetProperty("isError").GetBoolean());
    }
}
