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
    private static async Task VerifyBusAliasConnectivity(NativeClient client, DocumentSpecifier root,
        int processId, string display, CancellationToken token)
    {
        Task<SchematicHierarchyDataSnapshot> Read() =>
            client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
        var baseline = await Read();
        var rootScreen = baseline.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var childScreen = baseline.Data.Instances.First(s => s.Metadata.Document.SheetPath.Path.Count == 2);
        var template = rootScreen.Items.First(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
        var create = new ApplySchematicItemBatch { Document = root, ExpectedRevision = baseline.Revision,
            DocumentEpoch = baseline.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        KIID Id() => new() { Value = Guid.NewGuid().ToString("D") };
        Vector2 Point(long x, long y) => new() { XNm = x, YNm = y };
        var pins = new List<string>();
        foreach (var screen in new[] { rootScreen, childScreen })
        {
            var target = screen.Metadata.Document;
            void Add(Google.Protobuf.IMessage item) => create.Operations.Add(new SchematicItemOperation
                { TargetDocument = target.Clone(), Create = Any.Pack(item) });
            var probe = template.Clone(); probe.Id = Id(); probe.Position = Point(180000000, 140000000);
            probe.Path = target.SheetPath.Clone(); probe.Variants = new(); probe.InstanceRecords = new();
            int reference = screen == rootScreen ? 601 : 611;
            probe.ReferenceField.Text.Text_ = "TP" + reference;
            probe.ReferenceField.Text.Position = Point(180000000, 135000000);
            probe.ValueField.Text.Position = Point(180000000, 133000000);
            foreach (var instance in baseline.Data.Instances.Where(s => s.Metadata.ScreenId.Equals(screen.Metadata.ScreenId)))
            {
                var record = new SymbolSheetRecord { ProjectName = root.Project.Name,
                    Reference = "TP" + reference++, Unit = probe.Unit.Unit, Variants = new() };
                record.Path.Add(instance.Metadata.Document.SheetPath.Path.Select(id => id.Clone()));
                probe.InstanceRecords.Records.Add(record);
            }
            foreach (var definition in probe.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = definition.Item.Unpack<SchematicPin>(); pin.Id = Id();
                definition.Item = Any.Pack(pin); pins.Add(pin.Id.Value);
            }
            Add(probe);
            Add(new SchematicLine { Id = Id(), Type = SchematicLineType.SltWire,
                Start = probe.Position.Clone(), End = Point(185000000, 140000000) });
            Add(new BusEntry { Id = Id(), Type = BusEntryType.BetWireToBus,
                Position = Point(185000000, 140000000), Size = Point(2540000, 2540000) });
            Add(new SchematicLine { Id = Id(), Type = SchematicLineType.SltBus,
                Start = Point(187540000, 137540000), End = Point(187540000, 147540000) });
            Text Text(string value) => new() { Text_ = value, Attributes = new() { Size = Point(1270000, 1270000) } };
            Add(new LocalLabel { Id = Id(), Position = probe.Position.Clone(), Text = Text("QUALIFY") });
            Add(new GlobalLabel { Id = Id(), Position = Point(187540000, 137540000),
                Text = Text("{AUTOMATION_BUS}"), Shape = SchematicLabelShape.SlshBidi });
        }
        Assert.AreEqual(2, pins.Count, "The bus fixture requires one electrical pin per probe.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(create, token);
        var disconnected = await Read();
        async Task Connected(bool expected)
        {
            var nets = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = root }, token);
            var first = nets.Nets.Where(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == pins[0])).ToArray();
            Assert.AreEqual(1, first.Length);
            Assert.AreEqual(expected, first[0].Sheets.SelectMany(s => s.Items).Any(id => id.Value == pins[1]),
                "The shared global bus must connect the sheet-local probe nets only when its alias includes QUALIFY.");
        }
        await Connected(false);
        var desired = disconnected.Data.Clone();
        foreach (var screen in desired.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "AUTOMATION_BUS", Members = { "QUALIFY" } });
        var connect = new ApplySchematicItemBatch { Document = root, ExpectedRevision = disconnected.Revision,
            DocumentEpoch = disconnected.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        connect.Operations.Add(SchematicHierarchyDelta.Plan(disconnected.Data, desired));
        var rejected = connect.Clone(); rejected.OperationId = Guid.NewGuid().ToString("D");
        rejected.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        Assert.AreEqual(disconnected, await Read()); await Connected(false);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(connect, token);
        await Connected(true);
        async Task History(string key, SchematicHierarchyData expected)
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicHierarchyDataSnapshot next;
            do
            {
                next = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, deadline.Token);
                if (next.Revision.Equals(before.Revision)) await Task.Delay(100, deadline.Token);
            } while (next.Revision.Equals(before.Revision));
            Assert.IsTrue(expected.Equals(next.Data), "Bus journey undo/redo must preserve all supported design state.");
        }
        var connected = await Read();
        await History("z", disconnected.Data); await Connected(false);
        await History("y", connected.Data); await Connected(true);
        await History("z", disconnected.Data); await Connected(false);
        await History("z", baseline.Data);
    }
}
