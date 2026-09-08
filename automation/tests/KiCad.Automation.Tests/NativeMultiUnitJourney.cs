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
    private static async Task VerifyMultiUnitDefinition(NativeClient client, DocumentSpecifier document,
        SchematicSymbolInstance template, int processId, string display, string evidence, CancellationToken token)
    {
        var header = new ItemHeader { Document = document };
        var placements = new List<SchematicSymbolInstance>();
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Two-unit component fixture" };
        var libraryPins = new Dictionary<(int Unit, int Style), string>();
        for (int pinUnit = 1; pinUnit <= 2; ++pinUnit)
        for (int style = 1; style <= 2; ++style)
            libraryPins.Add((pinUnit, style), Guid.NewGuid().ToString("D"));
        for (int unit = 1; unit <= 2; ++unit)
        {
            var symbol = template.Clone();
            symbol.Id.Value = Guid.NewGuid().ToString("D");
            symbol.Unit.Unit = unit;
            symbol.BodyStyle = new SchematicSymbolBodyStyle { Style = 1 };
            symbol.Position = new Vector2 { XNm = 140000000, YNm = 40000000 + unit * 20000000 };
            symbol.ReferenceField.Text.Text_ = "U1";
            // Placement fields are absolute schematic coordinates. Keep each
            // unit's label separate from the template and from its test artwork.
            symbol.ReferenceField.Text.Position = new Vector2
                { XNm = symbol.Position.XNm - 12000000, YNm = symbol.Position.YNm };
            symbol.ValueField.Text.Position = new Vector2
                { XNm = symbol.Position.XNm - 12000000, YNm = symbol.Position.YNm - 4000000 };
            symbol.Definition.UnitCount = 2;
            symbol.PinMapOverride = new PinMapInstanceOverride
            {
                Mode = unit == 1 ? PinMapOverrideMode.PmomForceIdentity : PinMapOverrideMode.PmomDelegateToUnit1
            };
            if (unit == 1)
                symbol.PinMapOverride.Edits.Add(new PinMapEntry { PinNumber = "1", PadNumber = "7" });
            symbol.Definition.BodyStyle.Clear();
            symbol.Definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Base" });
            symbol.Definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Alternate" });
            var pinTemplate = symbol.Definition.Items.Single(i => i.Item.Is(SchematicPin.Descriptor));
            symbol.Definition.Items.Remove(pinTemplate);
            for (int pinUnit = 1; pinUnit <= 2; ++pinUnit)
            for (int style = 1; style <= 2; ++style)
            {
                var child = pinTemplate.Clone();
                child.Unit = new SchematicSymbolUnit { Unit = pinUnit };
                child.BodyStyle = new SchematicSymbolBodyStyle { Style = style };
                var pin = child.Item.Unpack<SchematicPin>();
                pin.Id.Value = Guid.NewGuid().ToString("D");
                pin.LibraryPinId = new KIID { Value = libraryPins[(pinUnit, style)] };
                if (style != symbol.BodyStyle.Style)
                {
                    // Inactive styles have owned definition pins but no
                    // persisted placed-pin identities until activated.
                    pin.Id = pin.LibraryPinId.Clone();
                    pin.LibraryPinId = null;
                }
                pin.Number = pinUnit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                pin.Position = new Vector2 { XNm = pinUnit * 2540000, YNm = style * 1270000 };
                child.Item = Any.Pack(pin);
                symbol.Definition.Items.Add(child);
            }
            placements.Add(symbol);
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
        }
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var first = await Read(placements[0].Id);
        var second = await Read(placements[1].Id);
        CheckDefinition(first);
        CheckDefinition(second);
        Assert.AreEqual(1, first.Unit.Unit);
        Assert.AreEqual(2, second.Unit.Unit);
        Assert.AreEqual("U1", first.ReferenceField.Text.Text_);
        Assert.AreEqual("U1", second.ReferenceField.Text.Text_);
        Assert.AreEqual(PinMapOverrideMode.PmomDelegateToUnit1, second.PinMapOverride.Mode,
            "Snapshots must preserve the persisted delegation, not its current resolved mapping.");
        Assert.AreEqual(second, SchematicDataXml.Read(SchematicDataXml.Write(second)));
        await CheckActivePins(first, second);
        await VerifyMultiUnitReferenceDisplay(client, document, first, second, processId, display, evidence, token);

        var updated = first.Clone();
        updated.Position.XNm += 5000000;
        updated.BodyStyle.Style = 2;
        updated.PinMapOverride.Edits[0].PadNumber = "8";
        var update = new ApplySchematicItemBatch { Document = document, Description = "Change one component unit" };
        update.Operations.Add(new SchematicItemOperation { Update = Any.Pack(updated) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(update, token);
        var changed = await Read(first.Id);
        CheckDefinition(changed);
        Assert.AreEqual(2, changed.BodyStyle.Style);
        Assert.AreEqual(updated.Position, changed.Position);
        Assert.AreEqual(OwnedDefinition(first), OwnedDefinition(changed),
            "Selecting another body style must not rewrite any unit's definition.");
        Assert.AreEqual(second, await Read(second.Id), "Editing unit A must not rewrite unit B.");
        await CheckActivePins(changed, second);

        // Undo style/placement, then undo both unit creations together.
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
        var cursor = new ReadSchematicChangeJournal { Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence };
        for (int undo = 0; undo < 2; ++undo)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "z");
            using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicChangeJournal history;
            do
            {
                history = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                if (history.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
            } while (history.Changes.Count == 0);
            cursor.AfterSequence = history.Sequence;
            if (undo == 0)
            {
                Assert.AreEqual(first, await Read(first.Id));
                Assert.AreEqual(second, await Read(second.Id));
            }
        }
        var netlist = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token);
        Assert.AreEqual(1, netlist.Nets.Count, "Undo must remove both temporary units from connectivity.");

        async Task<SchematicSymbolInstance> Read(KIID id)
        {
            var query = new GetItemsById { Header = header };
            query.Items.Add(id);
            return (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicSymbolInstance>();
        }

        async Task CheckActivePins(params SchematicSymbolInstance[] symbols)
        {
            var nets = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token);
            Assert.AreEqual(3, nets.Nets.Count, "Only the selected pin of each unit should enter connectivity.");
            var connectedIds = nets.Nets.SelectMany(n => n.Sheets).SelectMany(s => s.Items)
                .Select(i => i.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            foreach (var child in symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
            {
                bool active = child.Unit.Unit == symbol.Unit.Unit && child.BodyStyle.Style == symbol.BodyStyle.Style;
                Assert.AreEqual(active, connectedIds.Contains(child.Item.Unpack<SchematicPin>().Id.Value),
                    $"Unit {symbol.Unit.Unit}, style {symbol.BodyStyle.Style}, pin definition {child.Unit.Unit}:{child.BodyStyle.Style}");
            }
        }

        static SchematicSymbol OwnedDefinition(SchematicSymbolInstance symbol)
        {
            var definition = symbol.Definition.Clone();
            var children = definition.Items.ToArray();
            foreach (var child in children.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                // The native definition owns this ID. Active placement IDs
                // are verified independently against native connectivity and undo.
                if (pin.LibraryPinId is not null) pin.Id = pin.LibraryPinId.Clone();
                pin.LibraryPinId = null;
                child.Item = Any.Pack(pin);
            }
            definition.Items.Clear();
            definition.Items.Add(children.OrderBy(c => c.Item.Is(SchematicPin.Descriptor)
                ? c.Item.Unpack<SchematicPin>().Id.Value : c.Item.ToString(), StringComparer.Ordinal));
            return definition;
        }

        static void CheckDefinition(SchematicSymbolInstance symbol)
        {
            Assert.AreEqual(2u, symbol.Definition.UnitCount);
            Assert.AreEqual(2, symbol.Definition.BodyStyle.Count);
            var pins = symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)).ToArray();
            Assert.AreEqual(4, pins.Length, "All units and alternate body styles must remain reconstructible.");
            CollectionAssert.AreEquivalent(new[] { "1:1", "1:2", "2:1", "2:2" },
                pins.Select(p => $"{p.Unit.Unit}:{p.BodyStyle.Style}").ToArray());
            Assert.AreEqual(4, pins.Select(p => p.Item.Unpack<SchematicPin>().Id.Value).Distinct().Count());
            foreach (var child in pins)
                Assert.AreEqual(new Vector2 { XNm = child.Unit.Unit * 2540000, YNm = child.BodyStyle.Style * 1270000 },
                    child.Item.Unpack<SchematicPin>().Position);
        }
    }
}
