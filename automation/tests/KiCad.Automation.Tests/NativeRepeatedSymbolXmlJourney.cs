using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyRepeatedSymbolXml(NativeClient client, DocumentSpecifier root,
        ElectricalFixture electrical, HierarchyFixture hierarchy, string evidence, string instanceId,
        int processId, string display, CancellationToken token)
    {
        var rootQuery = new GetItemsById { Header = new ItemHeader { Document = root } };
        rootQuery.Items.Add(new KIID { Value = electrical.Symbol });
        var source = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(rootQuery, token))
            .Items.Single().Unpack<SchematicSymbolInstance>();
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = hierarchy.Second });
        var symbol = source.Clone();
        symbol.Id.Value = Guid.NewGuid().ToString("D");
        symbol.Path = first.SheetPath.Clone();
        symbol.ReferenceField.Text.Text_ = "TP101";
        symbol.Variants = new SchematicSymbolVariants();
        foreach (var child in symbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id.Value = Guid.NewGuid().ToString("D");
            child.Item = Any.Pack(pin);
        }
        symbol.InstanceRecords = new SymbolSheetRecords();
        SymbolSheetRecord Record(DocumentSpecifier document, string reference)
        {
            var record = new SymbolSheetRecord
            {
                ProjectName = "fixture", Reference = reference, Unit = 1, Variants = new SchematicSymbolVariants()
            };
            record.Path.Add(document.SheetPath.Path.Select(id => id.Clone()));
            return record;
        }
        var firstRecord = Record(first, "TP101");
        var secondRecord = Record(second, "TP201");
        secondRecord.Variants.Variants.Add(new SchematicSymbolVariant
        {
            Name = "alternate", Attributes = new SchematicSymbolAttributes { DoNotPopulate = true },
            SymbolOverride = (source.LibraryId ?? source.Definition.Id).Clone(),
            PinMapOverride = new PinMapInstanceOverride { Mode = PinMapOverrideMode.PmomForceIdentity }
        });
        var foreign = Record(root, "TP901");
        foreign.ProjectName = "Shared assembly";
        foreign.Path.Clear(); foreign.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        symbol.InstanceRecords.Records.Add(new[] { firstRecord, secondRecord, foreign });

        async Task Activate(DocumentSpecifier document) =>
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token);
        async Task<SchematicSymbolInstance> Read(DocumentSpecifier document)
        {
            await Activate(document);
            var query = new GetItemsById { Header = new ItemHeader { Document = document } };
            query.Items.Add(symbol.Id);
            return (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicSymbolInstance>();
        }
        async Task Apply(SchematicItemOperation operation)
        {
            await Activate(first);
            var batch = new ApplySchematicItemBatch { Document = first, Description = "Repeated symbol XML reconstruction" };
            batch.Operations.Add(operation);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }

        bool failed = false;
        try
        {
            await Apply(new() { Create = Any.Pack(symbol) });
            var originalFirst = await Read(first);
            var originalSecond = await Read(second);
            Assert.AreEqual("TP101", originalFirst.ReferenceField.Text.Text_);
            Assert.AreEqual("TP201", originalSecond.ReferenceField.Text.Text_);
            Assert.AreEqual(3, originalFirst.InstanceRecords.Records.Count);
            Assert.AreEqual(originalFirst.InstanceRecords, originalSecond.InstanceRecords);
            Assert.IsTrue(originalSecond.Variants.Variants.Single().Attributes.DoNotPopulate);

            await VerifyHiddenSheetViews(client, root, first, second, symbol.Position, evidence, instanceId, token);

            await Activate(root);
            var beforeRepeat = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
            var desiredRepeat = beforeRepeat.Data.Clone();
            var parentData = desiredRepeat.Instances.Single(s => s.Metadata.Document.Equals(root));
            var thirdSheet = parentData.Items.First(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
            thirdSheet.Id.Value = Guid.NewGuid().ToString("D");
            parentData.Items.Add(Any.Pack(thirdSheet));
            var third = root.Clone(); third.SheetPath.Path.Add(thirdSheet.Id.Clone());
            var thirdData = desiredRepeat.Instances.Single(s => s.Metadata.Document.Equals(first)).Clone();
            thirdData.Metadata.Document = third.Clone(); desiredRepeat.Instances.Add(thirdData);
            var thirdRecord = Record(third, "TP301");
            foreach (var screen in desiredRepeat.Instances.Where(s => s.Metadata.ScreenId.Equals(thirdData.Metadata.ScreenId)))
                for (int i = 0; i < screen.Items.Count; ++i)
                {
                    if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                    var placed = screen.Items[i].Unpack<SchematicSymbolInstance>();
                    placed.InstanceRecords.Records.Add(thirdRecord.Clone());
                    // The native writer orders placements by their exact path.
                    var orderedRecords = placed.InstanceRecords.Records.OrderBy(r => r.Path.Count)
                        .ThenBy(r => string.Join("/", r.Path.Select(id => id.Value)), StringComparer.Ordinal).ToArray();
                    placed.InstanceRecords.Records.Clear(); placed.InstanceRecords.Records.Add(orderedRecords);
                    if (screen.Metadata.Document.Equals(third))
                    {
                        placed.Path = third.SheetPath.Clone(); placed.ReferenceField.Text.Text_ = "TP301";
                        placed.Unit.Unit = thirdRecord.Unit; placed.Variants = thirdRecord.Variants.Clone();
                    }
                    screen.Items[i] = Any.Pack(placed);
                }
            // Native snapshots sort screens by path and top-level objects by
            // identity; desired input order is not a schematic property.
            foreach (var screen in desiredRepeat.Instances)
            {
                var orderedItems = SchematicItemDelta.Index(screen.Items).OrderBy(pair => pair.Key).Select(pair => Any.Pack(pair.Value)).ToArray();
                screen.Items.Clear(); screen.Items.Add(orderedItems);
            }
            var orderedScreens = desiredRepeat.Instances.OrderBy(s => s.Metadata.Document.SheetPath.Path.Count)
                .ThenBy(s => string.Join("/", s.Metadata.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal).ToArray();
            desiredRepeat.Instances.Clear(); desiredRepeat.Instances.Add(orderedScreens);
            var repeatBatch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeRepeat.Revision,
                DocumentEpoch = beforeRepeat.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            repeatBatch.Operations.Add(SchematicHierarchyDelta.Plan(beforeRepeat.Data, desiredRepeat));
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(repeatBatch, token);
            var repeatedData = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
            string expectedRepeatXml = SchematicDataXml.Write(desiredRepeat), actualRepeatXml = SchematicDataXml.Write(repeatedData.Data);
            int difference = Enumerable.Range(0, Math.Min(expectedRepeatXml.Length, actualRepeatXml.Length))
                .FirstOrDefault(i => expectedRepeatXml[i] != actualRepeatXml[i]);
            Assert.IsTrue(desiredRepeat.Equals(repeatedData.Data), "Adding an instance must preserve shared content and all explicitly declared symbol placements. First XML difference: expected "
                + expectedRepeatXml.Substring(difference, Math.Min(180, expectedRepeatXml.Length - difference))
                + " actual " + actualRepeatXml.Substring(difference, Math.Min(180, actualRepeatXml.Length - difference)));
            var removeRepeat = new ApplySchematicItemBatch { Document = root, ExpectedRevision = repeatedData.Revision,
                DocumentEpoch = repeatedData.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            removeRepeat.Operations.Add(SchematicHierarchyDelta.Plan(repeatedData.Data, beforeRepeat.Data));
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(removeRepeat, token);
            Assert.IsTrue(beforeRepeat.Data.Equals((await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token)).Data));

            // Move one physical symbol while preserving distinct references,
            // assembly variants, pin-map overrides and foreign placement records.
            await Activate(root);
            var captured = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
            var desiredHierarchy = captured.Data.Clone();
            foreach (var screen in desiredHierarchy.Instances)
                for (int i = 0; i < screen.Items.Count; ++i)
                    if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
                    {
                        var placed = screen.Items[i].Unpack<SchematicSymbolInstance>();
                        if (!placed.Id.Equals(symbol.Id)) continue;
                        placed.Position.XNm += 1000000; screen.Items[i] = Any.Pack(placed);
                    }
            var physicalMove = SchematicHierarchyDelta.Plan(captured.Data, desiredHierarchy);
            Assert.AreEqual(2, physicalMove.Count);
            Assert.AreEqual(1, physicalMove.Count(o => o.Update is not null), "A repeated physical symbol must move only once.");
            Assert.AreEqual(1, physicalMove.Count(o => o.ReplaceLibraryCache is not null),
                "The shared screen cache must be preserved once, not once per sheet instance.");
            var moveBatch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = captured.Revision,
                DocumentEpoch = captured.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            moveBatch.Operations.Add(physicalMove);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(moveBatch, token);
            var movedHierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
            Assert.IsTrue(desiredHierarchy.Equals(movedHierarchy.Data), "Moving a shared symbol must preserve both projected references and all placement records.");
            await Apply(new() { Update = Any.Pack(originalFirst) });
            Assert.AreEqual(originalSecond, await Read(second));
            Assert.AreEqual(foreign, originalFirst.InstanceRecords.Records.Single(r => r.ProjectName == foreign.ProjectName));

            string xml = SchematicDataXml.Write(originalFirst);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-repeated-symbol.xml"), xml, token);
            await Apply(new() { Remove = symbol.Id });
            await Apply(new() { Create = Any.Pack((SchematicSymbolInstance)SchematicDataXml.Read(xml)) });
            Assert.AreEqual(originalFirst, await Read(first));
            Assert.AreEqual(originalSecond, await Read(second));

            // A normal current-sheet update must not erase other placements.
            var ordinary = originalFirst.Clone(); ordinary.InstanceRecords = null;
            ordinary.Position.XNm += 1000000;
            await Apply(new() { Update = Any.Pack(ordinary) });
            Assert.AreEqual(originalFirst.InstanceRecords, (await Read(second)).InstanceRecords);
            await Apply(new() { Update = Any.Pack(originalFirst) });

            foreach (int invalidCase in new[] { 0, 1, 2 })
            {
                var invalid = originalFirst.Clone();
                if (invalidCase == 0) invalid.InstanceRecords.Records.Add(invalid.InstanceRecords.Records[0].Clone());
                if (invalidCase == 1) invalid.InstanceRecords.Records[0].Path[0].Value = "not-a-native-uuid";
                if (invalidCase == 2) invalid.InstanceRecords.Records[0].Unit = 0;
                await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(new() { Update = Any.Pack(invalid) }));
                Assert.AreEqual(originalFirst, await Read(first));
                Assert.AreEqual(originalSecond, await Read(second));
            }

            // Explicit complete replacement removes foreign records, rather
            // than silently merging stale placements back into the snapshot.
            var withoutForeign = originalFirst.Clone();
            withoutForeign.InstanceRecords.Records.Remove(
                withoutForeign.InstanceRecords.Records.Single(r => r.ProjectName == foreign.ProjectName));
            await Apply(new() { Update = Any.Pack(withoutForeign) });
            Assert.AreEqual(2, (await Read(first)).InstanceRecords.Records.Count);
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                new() { Document = first }, token);
            var cursor = new ReadSchematicChangeJournal
            {
                Document = first, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
            };
            foreach (var (key, expected, kind) in new[]
            {
                ("z", originalFirst, SchematicChange.Types.Kind.Undo),
                ("y", withoutForeign, SchematicChange.Types.Kind.Redo)
            })
            {
                await Activate(first);
                NativeKeyboard.SchematicShortcut(display, processId, key);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                SchematicChangeJournal changed;
                do
                {
                    changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, limit.Token);
                    if (changed.Changes.Count == 0) await Task.Delay(100, limit.Token);
                } while (changed.Changes.Count == 0);
                Assert.AreEqual(kind, changed.Changes.Single().Kind);
                cursor.AfterSequence = changed.Sequence;
                Assert.AreEqual(expected, await Read(first));
                Assert.AreEqual(expected.InstanceRecords, (await Read(second)).InstanceRecords);
            }
            await Apply(new() { Update = Any.Pack(originalFirst) });
            Assert.AreEqual(originalFirst, await Read(first));
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = first }, token);
            string saved = await File.ReadAllTextAsync(Path.Combine(hierarchy.Directory, "shared-child.kicad_sch"), token);
            StringAssert.Contains(saved, "\"Shared assembly\"");
            StringAssert.Contains(saved, "\"TP901\"");
            StringAssert.Contains(saved, "\"TP101\"");
            StringAssert.Contains(saved, "\"TP201\"");
            StringAssert.Contains(saved, "\"alternate\"");
            await Apply(new() { Remove = symbol.Id });
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = first }, token);
        }
        catch
        {
            failed = true;
            try
            {
                await NativeKeyboard.CaptureAsync(display,
                    Path.Combine(evidence, instanceId + "-repeated-symbol-failed.png"), CancellationToken.None);
                NativeKeyboard.SchematicShortcut(display, processId, "", describe: Console.WriteLine);
            }
            catch (Exception captureError) { Console.Error.WriteLine("Repeated-symbol failure capture unavailable: " + captureError.Message); }
            throw;
        }
        finally
        {
            try { await Activate(root); }
            catch (Exception cleanupError) when (failed)
            { Console.Error.WriteLine("Repeated-symbol cleanup failed: " + cleanupError.Message); }
        }
    }
}
