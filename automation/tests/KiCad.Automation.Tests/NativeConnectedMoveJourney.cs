using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyConnectedSymbolMove(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, string textId, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        var header = new ItemHeader { Document = document };
        Task<SchematicScreenDataSnapshot> Snapshot() => client.InvokeAsync<ReadSchematicScreenData,
            SchematicScreenDataSnapshot>(new() { Document = document }, token);
        Task<SelectionResponse> Selection() => client.InvokeAsync<GetSelection, SelectionResponse>(new() { Header = header }, token);
        SchematicSymbolInstance Symbol(SchematicScreenDataSnapshot value) => value.Data.Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(s => s.Id.Value == fixture.Symbol);
        async Task Connected()
        {
            var nets = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token);
            var a = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == fixture.PinA));
            var b = nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(i => i.Value == fixture.PinB));
            Assert.AreEqual(a.Name, b.Name, "Connected drag must preserve the fixture's pin-to-pin net.");
        }
        ApplySchematicItemBatch Move(SchematicScreenDataSnapshot baseline)
        {
            var move = new SchematicConnectedSymbolMove { Delta = new() { XNm = 2540000, YNm = 2540000 } };
            move.Symbols.Add(new KIID { Value = fixture.Symbol });
            var request = new ApplySchematicItemBatch
            {
                Document = document, DocumentEpoch = baseline.Revision.Epoch, ExpectedRevision = baseline.Revision,
                OperationId = Guid.NewGuid().ToString("D"), Description = "Connected placement journey"
            };
            request.Operations.Add(new SchematicItemOperation { MoveConnectedSymbols = move });
            return request;
        }
        async Task Undo(SchematicScreenDataSnapshot changed, SchematicScreenData expected)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await FocusedSchematicShortcut(client, document, processId, display, "z", deadline.Token);
                while (true)
                {
                    try
                    {
                        var restored = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                            new() { Document = document }, deadline.Token);
                        if (restored.Revision.Sequence != changed.Revision.Sequence)
                        {
                            Assert.AreEqual(expected, restored.Data, "Undo must restore symbols, wires, labels and junctions together.");
                            break;
                        }
                    }
                    catch (NativeApiException error) when (error.Status == 7) { }
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch
            {
                try
                {
                    await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence,
                        instanceId + "-connected-undo-failed-" + changed.Revision.Sequence + ".png"), CancellationToken.None);
                    NativeKeyboard.HasWindow(display, processId, "Schematic Editor", Console.WriteLine);
                }
                catch (Exception captureError) { Console.Error.WriteLine("Undo capture unavailable: " + captureError.Message); }
                throw;
            }
        }

        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
        var select = new AddToSelection { Header = header }; select.Items.Add(new KIID { Value = textId });
        await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        var selection = await Selection();
        var before = await Snapshot();
        await Connected();

        // A later failure must roll back an already performed native drag,
        // including newly created wire bends and the user's original selection.
        var rejected = Move(before);
        rejected.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        Assert.AreEqual(before, await Snapshot());
        Assert.AreEqual(selection, await Selection());
        await Connected();

        foreach (long displacement in new[] { 1L, long.MaxValue })
        {
            var invalid = Move(before); invalid.Operations[0].MoveConnectedSymbols.Delta.XNm = displacement;
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalid, token));
            Assert.AreEqual(before, await Snapshot());
        }

        var request = Move(before);
        var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token);
        var moved = await Snapshot();
        Assert.AreEqual(Symbol(before).Position.XNm + 2540000, Symbol(moved).Position.XNm);
        Assert.AreEqual(Symbol(before).Position.YNm + 2540000, Symbol(moved).Position.YNm);
        Assert.IsTrue(moved.Revision.Sequence > before.Revision.Sequence);
        Assert.IsTrue(result.Items.Any(i => i.Is(SchematicLine.Descriptor)), "Return affected wires, not only the requested symbol.");
        Assert.AreEqual(selection, await Selection());
        await Connected();
        var replay = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token);
        Assert.AreEqual(result, replay);
        Assert.AreEqual(moved, await Snapshot(), "A timeout retry must not drag the symbol twice.");
        var stale = Move(before);
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        Assert.AreEqual(moved, await Snapshot());
        var preview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-connected-move.png"), preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connected-move.xml"), SchematicDataXml.Write(moved.Data), token);
        await Undo(moved, before.Data);
        await Connected();

        var unlocked = await Snapshot();
        var locked = Symbol(unlocked).Clone(); locked.Locked = (LockedState)2;
        var lockRequest = new ApplySchematicItemBatch { Document = document };
        lockRequest.Operations.Add(new SchematicItemOperation { Update = Any.Pack(locked) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(lockRequest, token);
        var lockedSnapshot = await Snapshot();
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch,
            SchematicItemBatchResult>(Move(lockedSnapshot), token));
        Assert.AreEqual(lockedSnapshot, await Snapshot());
        await Undo(lockedSnapshot, unlocked.Data);

        var wireBaseline = await Snapshot();
        var lockedWire = wireBaseline.Data.Items.Where(i => i.Is(SchematicLine.Descriptor))
            .Select(i => i.Unpack<SchematicLine>()).Single(w => w.Id.Value == fixture.Wire).Clone();
        lockedWire.Locked = (LockedState)2;
        var lockWire = new ApplySchematicItemBatch { Document = document };
        lockWire.Operations.Add(new SchematicItemOperation { Update = Any.Pack(lockedWire) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(lockWire, token);
        var lockedWireSnapshot = await Snapshot();
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch,
            SchematicItemBatchResult>(Move(lockedWireSnapshot), token));
        Assert.AreEqual(lockedWireSnapshot, await Snapshot(), "A connected locked wire must reject the move without leaving transient edit state.");
        await Undo(lockedWireSnapshot, wireBaseline.Data);

        var textBaseline = await Snapshot();
        var lockedText = textBaseline.Data.Items.Where(i => i.Is(SchematicText.Descriptor))
            .Select(i => i.Unpack<SchematicText>()).Single(t => t.Id.Value == textId).Clone();
        lockedText.Locked = (LockedState)2;
        var lockText = new ApplySchematicItemBatch { Document = document };
        lockText.Operations.Add(new SchematicItemOperation { Update = Any.Pack(lockedText) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(lockText, token);
        var lockedTextSnapshot = await Snapshot();
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(Move(lockedTextSnapshot), token);
        var movedPastLockedText = await Snapshot();
        Assert.AreEqual(lockedText, movedPastLockedText.Data.Items.Where(i => i.Is(SchematicText.Descriptor))
            .Select(i => i.Unpack<SchematicText>()).Single(t => t.Id.Value == textId));
        await Undo(movedPastLockedText, lockedTextSnapshot.Data);
        await Undo(await Snapshot(), textBaseline.Data);
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
    }
}
