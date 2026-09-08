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
    private static async Task VerifyTransformedPins(NativeClient client, DocumentSpecifier document,
        SchematicSymbolInstance template, int processId, string display, string evidence, string instanceId,
        CancellationToken token)
    {
        var netQuery = new GetSchematicNetlist { Document = document };
        var baseline = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(netQuery, token);
        Assert.AreEqual(1, baseline.Nets.Count);
        var baselineIds = baseline.Nets.Single().Sheets.SelectMany(s => s.Items).Select(i => i.Value).Order().ToArray();
        for (int rotation = 0; rotation < 4; ++rotation)
        for (int mirrors = 0; mirrors < 4; ++mirrors)
        {
            string caseName = $"rotation={rotation * 90}, mirrors={mirrors}";
            var symbol = template.Clone();
            symbol.Id.Value = Guid.NewGuid().ToString("D");
            symbol.Position = new Vector2 { XNm = 130000000, YNm = 100000000 };
            symbol.ReferenceField.Text.Text_ = "TP3";
            symbol.ReferenceField.Text.Position = new Vector2 { XNm = 130000000, YNm = 95000000 };
            symbol.ValueField.Text.Position = new Vector2 { XNm = 130000000, YNm = 93000000 };
            symbol.Transform = new SchematicSymbolTransform
            {
                Orientation = (SchematicSymbolOrientation)(rotation + 1),
                MirrorX = (mirrors & 1) != 0, MirrorY = (mirrors & 2) != 0
            };
            var pinChild = symbol.Definition.Items.Single(i => i.Item.Is(SchematicPin.Descriptor));
            var pin = pinChild.Item.Unpack<SchematicPin>();
            pin.Id.Value = Guid.NewGuid().ToString("D");
            pin.Position = new Vector2 { XNm = 2540000, YNm = 1270000 };
            pinChild.Item = Any.Pack(pin);
            Assert.IsTrue(symbol.Definition.PinsUseLocalCoordinates);

            // Independent expected sheet-space geometry: positive rotations are
            // counterclockwise on the screen; mirror X/Y reflect across those axes.
            (long x, long y) = rotation switch
            {
                0 => (2540000L, 1270000L), 1 => (1270000L, -2540000L),
                2 => (-2540000L, -1270000L), _ => (-1270000L, 2540000L)
            };
            if (symbol.Transform.MirrorX) y = -y;
            if (symbol.Transform.MirrorY) x = -x;
            var endpoint = new Vector2 { XNm = symbol.Position.XNm + x, YNm = symbol.Position.YNm + y };
            var horizontal = new SchematicLine
            {
                Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Type = (SchematicLineType)1,
                Start = endpoint, End = new Vector2 { XNm = 100000000, YNm = endpoint.YNm }
            };
            var vertical = new SchematicLine
            {
                Id = new KIID { Value = Guid.NewGuid().ToString("D") }, Type = (SchematicLineType)1,
                Start = horizontal.End.Clone(), End = new Vector2 { XNm = 100000000, YNm = 80000000 }
            };
            var batch = new ApplySchematicItemBatch { Document = document, Description = "Transformed pin fixture" };
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(symbol) });
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(horizontal) });
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(vertical) });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            var nets = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(netQuery, token);
            Assert.AreEqual(1, nets.Nets.Count, caseName);
            Assert.IsTrue(nets.Nets.Single().Sheets.SelectMany(s => s.Items).Any(i => i.Value == pin.Id.Value), caseName);
            var query = new GetItemsById { Header = new ItemHeader { Document = document } };
            query.Items.Add(symbol.Id);
            var returned = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicSymbolInstance>();
            Assert.AreEqual(pin.Position, returned.Definition.Items.Single(i => i.Item.Is(SchematicPin.Descriptor))
                .Item.Unpack<SchematicPin>().Position, caseName);
            // Exercise normalized output as input, alternating current and
            // legacy pin coordinates across the complete transform matrix.
            if ((mirrors & 1) != 0)
            {
                returned.Definition.PinsUseLocalCoordinates = false;
                var returnedPin = returned.Definition.Items.Single(i => i.Item.Is(SchematicPin.Descriptor));
                var legacy = returnedPin.Item.Unpack<SchematicPin>();
                legacy.Position = endpoint.Clone();
                returnedPin.Item = Any.Pack(legacy);
            }
            var echo = new ApplySchematicItemBatch { Document = document, Description = "Round-trip transformed probe" };
            // Rendering must not make an unchanged symbol update leave stale
            // connection-graph references to the replaced pin allocations.
            await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
            echo.Operations.Add(new SchematicItemOperation { Update = Any.Pack(returned) });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(echo, token);
            var roundTripNets = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(netQuery, token);
            Assert.AreEqual(1, roundTripNets.Nets.Count, caseName);
            Assert.IsTrue(roundTripNets.Nets.Single().Sheets.SelectMany(s => s.Items).Any(i => i.Value == pin.Id.Value), caseName);
            if (rotation == 1 && mirrors == 1)
            {
                var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
                await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-rotated-mirrored.png"), image.Png.ToByteArray(), token);
            }
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
            var cursor = new ReadSchematicChangeJournal { Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence };
            for (int undo = 0; undo < 2; ++undo)
            {
                NativeKeyboard.SchematicShortcut(display, processId, "z");
                using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                SchematicChangeJournal changed;
                do
                {
                    changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                    if (changed.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
                } while (changed.Changes.Count == 0);
                Assert.AreEqual(1, changed.Changes.Count, caseName);
                cursor.AfterSequence = changed.Sequence;
            }
            var restored = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(netQuery, token);
            Assert.AreEqual(1, restored.Nets.Count, caseName);
            CollectionAssert.AreEqual(baselineIds,
                restored.Nets.Single().Sheets.SelectMany(s => s.Items).Select(i => i.Value).Order().ToArray(), caseName);
        }
    }
}
