using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyManualMoveRoundTrip(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, int processId, string display, string evidence, string instanceId,
        CancellationToken token)
    {
        var header = new ItemHeader { Document = document };
        async Task<SchematicScreenDataSnapshot> Snapshot(CancellationToken local) =>
            await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, local);
        async Task<SchematicNetlistResponse> Nets() =>
            await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token);
        string NetOf(SchematicNetlistResponse nets, string pin) =>
            nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == pin)).Name;
        string[] Topology(SchematicNetlistResponse nets) => nets.Nets.SelectMany(n => n.Sheets
            .SelectMany(s => s.Items.Select(id => n.Name + "\n" + SchematicJson.Formatter.Format(s.Path) + "\n" + id.Value)))
            .Order(StringComparer.Ordinal).ToArray();
        SchematicSymbolInstance Symbol(SchematicScreenDataSnapshot snapshot) => snapshot.Data.Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(i => i.Id.Value == fixture.Symbol);

        var before = await Snapshot(token);
        var netsBefore = await Nets();
        Assert.AreEqual(NetOf(netsBefore, fixture.PinA), NetOf(netsBefore, fixture.PinB));
        var select = new AddToSelection { Header = header };
        select.Items.Add(new KIID { Value = fixture.Symbol });
        await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        NativeKeyboard.SchematicShortcut(display, processId, "m", controlKey: false, focusCanvas: false);
        try
        {
            using var begin = CancellationTokenSource.CreateLinkedTokenSource(token);
            begin.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { await Snapshot(begin.Token); }
                catch (NativeApiException error) when (error.Status == 7)
                { StringAssert.Contains(error.Message, "current schematic edit"); break; }
                await Task.Delay(100, begin.Token);
            }
            // Commit through a real canvas click. M is deliberately a move,
            // not a connected drag: this electrical change must be observable.
            int pointerX = 620, pointerY = 460;
            // XSync confirms X-server delivery, not that the editor processed
            // motion. This legacy raw item query is only an input-progress
            // probe; it is deliberately NOT a matched committed observation.
            var query = new GetItemsById { Header = header };
            query.Items.Add(new KIID { Value = fixture.Symbol });
            using var motion = CancellationTokenSource.CreateLinkedTokenSource(token);
            motion.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                // SetCursorPosition deliberately suppresses the next warp
                // motion. Send a short physical path, as a mouse actually
                // produces, rather than one teleport followed by a click.
                NativeKeyboard.SchematicShortcut(display, processId, "motion", controlKey: false,
                    clickFromLeft: pointerX, clickFromTop: pointerY);
                await Task.Delay(100, motion.Token);
                var live = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, motion.Token))
                    .Items.Single().Unpack<SchematicSymbolInstance>();
                if (!live.Position.Equals(Symbol(before).Position)) break;
                pointerX = pointerX == 640 ? 620 : pointerX + 10;
                pointerY = pointerY == 480 ? 460 : pointerY + 10;
            }
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
                clickFromLeft: pointerX, clickFromTop: pointerY);
            var moved = await Ready();
            Assert.AreNotEqual(Symbol(before).Position, Symbol(moved).Position);
            Assert.AreEqual(before.Revision.Epoch, moved.Revision.Epoch);
            Assert.IsTrue(moved.Revision.Sequence > before.Revision.Sequence);
            var netsAfter = await Nets();
            Assert.AreNotEqual(NetOf(netsAfter, fixture.PinA), NetOf(netsAfter, fixture.PinB),
                "Moving the symbol away from its stationary wire must expose the resulting disconnection.");
            var electrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new()
                { Document = document, ExpectedRevision = moved.Revision }, token);
            Assert.AreEqual(moved.Revision, electrical.Hierarchy.Revision);
            Assert.AreEqual(moved.Data, electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document)));
            bool Connected(SchematicElectricalState state) => state.Nets.Any(n =>
                n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinA)
                && n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinB));
            Assert.IsFalse(Connected(electrical), "The same-revision electrical read must include the real manual disconnection.");
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-electrical-state.json"),
                SchematicJson.Formatter.Format(electrical), token);
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            {
                Document = document, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence
            }, token);
            Assert.IsTrue(journal.Changes.Any(c => c.Kind == SchematicChange.Types.Kind.Commit));
            Assert.IsTrue(journal.Changes.All(c => c.OriginId.Length == 0 && c.OperationId.Length == 0),
                "A manual edit must not inherit a preceding automation operation's attribution.");
            string xml = SchematicDataXml.Write(moved.Data);
            Assert.AreEqual(moved.Data, SchematicDataXml.Read(xml));
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-move.xml"), xml, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-move-netlist.json"),
                SchematicJson.Formatter.Format(netsAfter), token);
            var preview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-manual-move.png"), preview.Png.ToByteArray(), token);

            NativeKeyboard.SchematicShortcut(display, processId, "z");
            using var undo = CancellationTokenSource.CreateLinkedTokenSource(token);
            undo.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot restored;
            do
            {
                restored = await Snapshot(undo.Token);
                if (restored.Revision.Sequence == moved.Revision.Sequence) await Task.Delay(100, undo.Token);
            } while (restored.Revision.Sequence == moved.Revision.Sequence);
            Assert.AreEqual(before.Data, restored.Data, "Native undo must restore the manually moved symbol and all supported data.");
            CollectionAssert.AreEqual(Topology(netsBefore), Topology(await Nets()));
            var restoredElectrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new()
                { Document = document, ExpectedRevision = restored.Revision }, token);
            Assert.AreEqual(restored.Revision, restoredElectrical.Hierarchy.Revision);
            Assert.IsTrue(Connected(restoredElectrical));
        }
        catch
        {
            try { await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-manual-move-failed.png"), CancellationToken.None); }
            catch (Exception error) { Console.Error.WriteLine("Manual-move failure capture unavailable: " + error.Message); }
            throw;
        }
        finally
        {
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
        }
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);

        async Task<SchematicScreenDataSnapshot> Ready()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try
                {
                    var snapshot = await Snapshot(deadline.Token);
                    if (snapshot.Revision.Sequence != before.Revision.Sequence) return snapshot;
                }
                catch (NativeApiException error) when (error.Status == 7) { }
                await Task.Delay(100, deadline.Token);
            }
        }
    }
}
