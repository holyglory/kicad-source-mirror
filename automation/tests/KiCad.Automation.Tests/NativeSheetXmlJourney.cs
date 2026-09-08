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
    private static async Task VerifySheetXml(NativeClient client, DocumentSpecifier root,
        HierarchyFixture hierarchy, string evidence, string instanceId, int processId, string display,
        CancellationToken token)
    {
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = hierarchy.Second });
        async Task Activate(DocumentSpecifier document) =>
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token);
        async Task<Any> Read(DocumentSpecifier document, KIID id)
        {
            await Activate(document);
            var query = new GetItemsById { Header = new ItemHeader { Document = document } };
            query.Items.Add(id);
            return (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single();
        }
        async Task Apply(DocumentSpecifier document, SchematicItemOperation operation)
        {
            await Activate(document);
            var batch = new ApplySchematicItemBatch { Document = document, Description = "Shared sheet XML reconstruction" };
            batch.Operations.Add(operation);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }

        var template = (await Read(root, new KIID { Value = hierarchy.First })).Unpack<SheetSymbol>();
        var keeper = template.Clone(); keeper.Id.Value = Guid.NewGuid().ToString("D");
        keeper.NameField.Text.Text_ = "XML child dependency";
        keeper.FilenameField.Text.Text_ = "xml-grandchild.kicad_sch";
        keeper.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        keeper.PageNumber = "4";
        keeper.InstanceRecords = null; keeper.Variants = new SheetVariants();
        await Apply(root, new() { Create = Any.Pack(keeper) });
        var dependency = root.Clone(); dependency.SheetPath.Path.Add(keeper.Id);
        var text = (await Read(first, new KIID { Value = hierarchy.TextId })).Unpack<SchematicText>();
        text.Id.Value = Guid.NewGuid().ToString("D"); text.Text.Text_ = "Declared child content";
        await Apply(dependency, new() { Create = Any.Pack(text) });

        var nested = keeper.Clone(); nested.Id.Value = Guid.NewGuid().ToString("D");
        nested.Path = first.SheetPath.Clone(); nested.NameField.Text.Text_ = "Shared nested sheet";
        nested.PageNumber = "2.A"; nested.InstanceRecords = new SheetPlacementRecords();
        SheetPlacementRecord Record(DocumentSpecifier parent, string page)
        {
            var record = new SheetPlacementRecord
            {
                ProjectName = "fixture", PageNumber = page, Variants = new SheetVariants()
            };
            record.Path.Add(parent.SheetPath.Path.Select(id => id.Clone()));
            return record;
        }
        var firstRecord = Record(first, "2.A");
        var secondRecord = Record(second, "3.B");
        var variant = new SheetVariant { Name = "Optional channel", ExcludeFromSim = true, ExcludeFromBom = false, Dnp = true };
        variant.Fields.Add("Assembly note", "Leave channel unpopulated");
        secondRecord.Variants.Variants.Add(variant);
        var foreign = Record(root, "EXT-9"); foreign.ProjectName = "External assembly";
        foreign.Path.Clear(); foreign.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        var rootRecord = new SheetPlacementRecord { PageNumber = "ROOT-X", Variants = new SheetVariants() };
        nested.InstanceRecords.Records.Add(new[] { firstRecord, secondRecord, foreign, rootRecord });

        try
        {
            await Apply(first, new() { Create = Any.Pack(nested) });
            var originalFirst = (await Read(first, nested.Id)).Unpack<SheetSymbol>();
            var originalSecond = (await Read(second, nested.Id)).Unpack<SheetSymbol>();
            Assert.AreEqual("2.A", originalFirst.PageNumber);
            Assert.AreEqual("3.B", originalSecond.PageNumber);
            Assert.AreEqual(originalFirst.InstanceRecords, originalSecond.InstanceRecords);
            Assert.AreEqual(4, originalFirst.InstanceRecords.Records.Count);
            Assert.AreEqual(rootRecord, originalFirst.InstanceRecords.Records.Single(r => r.Path.Count == 0));
            Assert.IsTrue(originalSecond.Variants.Variants.Single().Dnp);
            Assert.AreEqual(foreign, originalFirst.InstanceRecords.Records.Single(r => r.ProjectName == foreign.ProjectName));
            string xml = SchematicDataXml.Write(originalFirst);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-shared-sheet.xml"), xml, token);
            await Apply(first, new() { Remove = nested.Id });
            await Apply(first, new() { Create = Any.Pack((SheetSymbol)SchematicDataXml.Read(xml)) });
            Assert.AreEqual(originalFirst, (await Read(first, nested.Id)).Unpack<SheetSymbol>());
            Assert.AreEqual(originalSecond, (await Read(second, nested.Id)).Unpack<SheetSymbol>());
            foreach (var parent in new[] { first, second })
            {
                var child = parent.Clone(); child.SheetPath.Path.Add(nested.Id);
                Assert.AreEqual(text, (await Read(child, text.Id)).Unpack<SchematicText>());
            }

            var ordinary = originalFirst.Clone(); ordinary.InstanceRecords = null;
            ordinary.Position.XNm += 1000000;
            await Apply(first, new() { Update = Any.Pack(ordinary) });
            Assert.AreEqual(originalFirst.InstanceRecords, (await Read(second, nested.Id)).Unpack<SheetSymbol>().InstanceRecords);
            await Apply(first, new() { Update = Any.Pack(originalFirst) });
            foreach (int invalidCase in new[] { 0, 1, 2 })
            {
                var invalid = originalFirst.Clone();
                if (invalidCase == 0) invalid.InstanceRecords.Records.Add(invalid.InstanceRecords.Records[0].Clone());
                if (invalidCase == 1) invalid.InstanceRecords.Records.First(r => r.Path.Count != 0).Path[0].Value = "invalid-uuid";
                if (invalidCase == 2)
                    invalid.InstanceRecords.Records.Single(r => r.Variants.Variants.Count != 0).Variants.Variants[0].ClearDnp();
                await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(first, new() { Update = Any.Pack(invalid) }));
                Assert.AreEqual(originalFirst, (await Read(first, nested.Id)).Unpack<SheetSymbol>());
                Assert.AreEqual(originalSecond, (await Read(second, nested.Id)).Unpack<SheetSymbol>());
            }

            var withoutForeign = originalFirst.Clone();
            withoutForeign.InstanceRecords.Records.Remove(
                withoutForeign.InstanceRecords.Records.Single(r => r.ProjectName == foreign.ProjectName));
            await Apply(first, new() { Update = Any.Pack(withoutForeign) });
            Assert.AreEqual(3, (await Read(first, nested.Id)).Unpack<SheetSymbol>().InstanceRecords.Records.Count);
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = first }, token);
            var cursor = new ReadSchematicChangeJournal
            {
                Document = first, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
            };
            foreach (var (key, expected) in new[] { ("z", originalFirst), ("y", withoutForeign) })
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
                cursor.AfterSequence = changed.Sequence;
                Assert.AreEqual(expected, (await Read(first, nested.Id)).Unpack<SheetSymbol>());
            }
            await Apply(first, new() { Update = Any.Pack(originalFirst) });
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = first }, token);
            string saved = await File.ReadAllTextAsync(Path.Combine(hierarchy.Directory, "shared-child.kicad_sch"), token);
            StringAssert.Contains(saved, "\"External assembly\"");
            StringAssert.Contains(saved, "\"EXT-9\"");
            StringAssert.Contains(saved, "\"3.B\"");
            StringAssert.Contains(saved, "\"Optional channel\"");
            await Apply(first, new() { Remove = nested.Id });
            await Apply(root, new() { Remove = keeper.Id });
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
        }
        finally { await Activate(root); }
    }
}
