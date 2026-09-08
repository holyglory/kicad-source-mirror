using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMcpConnectedMove(NativeClient native, DocumentSpecifier document,
        ElectricalFixture fixture, int processId, string display, string evidence, string instanceId,
        Func<string, object, bool, Task<JsonElement>> call, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Snapshot() => native.InvokeAsync<ReadSchematicScreenData,
            SchematicScreenDataSnapshot>(new() { Document = document }, token);
        SchematicSymbolInstance Symbol(SchematicScreenData data) => data.Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(s => s.Id.Value == fixture.Symbol);
        var before = await Snapshot();
        string operation = Guid.NewGuid().ToString("D");
        object Arguments(string operationId) => new
        {
            instanceId, documentJson = SchematicJson.Formatter.Format(document), symbolIds = new[] { fixture.Symbol },
            deltaXNm = 2540000L, deltaYNm = 2540000L, documentEpoch = before.Revision.Epoch,
            expectedRevision = before.Revision.Sequence, operationId
        };
        var result = await call("kicad_schematic_move_connected_symbols", Arguments(operation), false);
        var structured = result.GetProperty("structuredContent");
        Assert.AreEqual("Completed", structured.GetProperty("status").GetString());
        Assert.IsFalse(structured.GetProperty("trackingComplete").GetBoolean());
        var moved = await Snapshot();
        Assert.AreEqual(Symbol(before.Data).Position.XNm + 2540000, Symbol(moved.Data).Position.XNm);
        Assert.AreEqual(Symbol(before.Data).Position.YNm + 2540000, Symbol(moved.Data).Position.YNm);
        var nets = await native.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token);
        var a = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == fixture.PinA));
        var b = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == fixture.PinB));
        Assert.AreEqual(a.Name, b.Name, "The actual MCP operation must preserve the native pin-to-pin connection.");
        var replay = await call("kicad_schematic_move_connected_symbols", Arguments(operation), false);
        Assert.AreEqual(structured.GetProperty("nativeResult").GetRawText(),
            replay.GetProperty("structuredContent").GetProperty("nativeResult").GetRawText());
        Assert.AreEqual(moved, await Snapshot());
        var stale = await call("kicad_schematic_move_connected_symbols", Arguments(Guid.NewGuid().ToString("D")), true);
        Assert.AreEqual("NotConfirmed", stale.GetProperty("structuredContent").GetProperty("status").GetString());
        Assert.AreEqual(moved, await Snapshot());
        var observation = await call("kicad_schematic_observe", new
        { instanceId, documentJson = SchematicJson.Formatter.Format(document) }, false);
        var observed = SchematicJson.Parser.Parse<SchematicObservation>(observation.GetProperty("structuredContent")
            .GetProperty("observation").GetRawText());
        Assert.AreEqual(moved, observed.Snapshot);
        Assert.AreEqual(moved.Revision, observed.Preview.Revision);
        var image = observation.GetProperty("content").EnumerateArray().Single(c => c.GetProperty("type").GetString() == "image");
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-mcp-connected-move.png"),
            Convert.FromBase64String(image.GetProperty("data").GetString()!), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-mcp-connected-move.xml"),
            SchematicDataXml.Write(moved.Data), token);

        await FocusedSchematicShortcut(native, document, processId, display, "z", token);
        using var undo = CancellationTokenSource.CreateLinkedTokenSource(token);
        undo.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                var restored = await native.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                    new() { Document = document }, undo.Token);
                if (restored.Revision.Sequence != moved.Revision.Sequence)
                {
                    Assert.AreEqual(before.Data, restored.Data);
                    break;
                }
            }
            catch (NativeApiException error) when (error.Status == 7) { }
            await Task.Delay(100, undo.Token);
        }
    }
}
