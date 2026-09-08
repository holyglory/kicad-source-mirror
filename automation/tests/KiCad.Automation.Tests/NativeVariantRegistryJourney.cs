using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyVariantRegistryXml(NativeClient client, DocumentSpecifier root,
        string rootFile, int processId, string display, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = root }, token);
        var initial = await Read();
        var fixtureSymbol = initial.Data.Items.First(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Unpack<SchematicSymbolInstance>();
        fixtureSymbol.Variants ??= new();
        fixtureSymbol.Variants.Variants.Add(new SchematicSymbolVariant
            { Name = "Assembly", Description = "original", Attributes = new() });
        var placement = fixtureSymbol.InstanceRecords.Records.Single(r => r.ProjectName == "fixture"
            && r.Path.SequenceEqual(root.SheetPath.Path));
        placement.Variants = fixtureSymbol.Variants.Clone();
        foreach (var variant in placement.Variants.Variants) variant.ClearDescription();
        var setup = Batch(initial); setup.Operations.Add(new SchematicItemOperation { Update = Any.Pack(fixtureSymbol) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(setup, token);
        var baseline = await Read();
        var desired = baseline.Data.Clone();
        desired.Metadata.VariantDescriptions["Assembly"] = "From XML: place beside heat spreader";
        desired.Metadata.VariantDescriptions.Add("tooling", "");
        int symbolIndex = Enumerable.Range(0, desired.Items.Count).First(i =>
            desired.Items[i].Is(SchematicSymbolInstance.Descriptor)
            && desired.Items[i].Unpack<SchematicSymbolInstance>().Variants?.Variants.Count > 0);
        var moved = desired.Items[symbolIndex].Unpack<SchematicSymbolInstance>();
        string usedVariant = moved.Variants.Variants[0].Name;
        desired.Metadata.VariantDescriptions[usedVariant] = "XML description for placed component";
        moved.Position.XNm += 1000000;
        desired.Items[symbolIndex] = Any.Pack(moved);
        desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var expectedAfter = desired.Clone();
        for (int i = 0; i < expectedAfter.Items.Count; i++)
        {
            if (!expectedAfter.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var symbol = expectedAfter.Items[i].Unpack<SchematicSymbolInstance>();
            if (symbol.Variants is null) continue;
            foreach (var variant in symbol.Variants.Variants)
                if (expectedAfter.Metadata.VariantDescriptions.TryGetValue(variant.Name, out var description))
                    variant.Description = description;
            expectedAfter.Items[i] = Any.Pack(symbol);
        }
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot state) => new()
        {
            Document = root, ExpectedRevision = state.Revision, DocumentEpoch = state.Revision.Epoch,
            OperationId = Guid.NewGuid().ToString("D"), Description = "Edit variant registry from XML"
        };
        async Task Persisted(SchematicScreenData expected)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            var actual = project.RootElement.GetProperty("schematic").GetProperty("variants").EnumerateArray()
                .ToDictionary(v => v.GetProperty("name").GetString()!,
                    v => v.TryGetProperty("description", out var text) ? text.GetString()! : "");
            CollectionAssert.AreEquivalent(expected.Metadata.VariantDescriptions.ToArray(), actual.ToArray());
        }
        async Task UndoRedo(string key, SchematicScreenData expected)
        {
            var previous = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = root }, timeout.Token))
                .Revision.Equals(previous.Revision)) await Task.Delay(100, timeout.Token);
            Assert.AreEqual(expected, (await Read()).Data);
            await Persisted(expected);
        }
        var batch = Batch(baseline); batch.Operations.Add(SchematicItemDelta.Plan(baseline.Data, desired));
        var applied = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.IsTrue(applied.VariantRegistryChanged);
        var after = await Read(); Assert.AreEqual(expectedAfter, after.Data); await Persisted(expectedAfter);
        Assert.AreEqual(applied, await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token));
        Assert.AreEqual(after, await Read());
        var noop = Batch(after); noop.Operations.Add(batch.Operations.Single(o => o.ReplaceVariantRegistry is not null).Clone());
        Assert.IsFalse((await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(noop, token)).VariantRegistryChanged);
        Assert.AreEqual(after, await Read());

        var failed = Batch(after);
        failed.Operations.Add(new SchematicItemOperation { ReplaceVariantRegistry = new() });
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failed, token));
        Assert.AreEqual(after, await Read()); await Persisted(expectedAfter);
        foreach (var (name, value) in new[] { ("", "invalid"), (" spaced", ""), ("x\0y", ""), ("valid", "x\0y") })
        {
            var invalid = Batch(after); var registry = new SchematicVariantRegistryState();
            registry.Descriptions.Add(name, value);
            invalid.Operations.Add(new SchematicItemOperation { ReplaceVariantRegistry = registry });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalid, token));
            Assert.AreEqual(after, await Read());
        }
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = baseline.Revision.Epoch, AfterSequence = baseline.Revision.Sequence }, token);
        Assert.AreEqual(1, journal.Changes.Count);
        var competingXml = baseline.Data.Clone();
        competingXml.Metadata.VariantDescriptions[usedVariant] = "Explicitly chosen XML preference";
        var conflictingMove = competingXml.Items[symbolIndex].Unpack<SchematicSymbolInstance>();
        conflictingMove.Position.XNm += 2000000;
        competingXml.Items[symbolIndex] = Any.Pack(conflictingMove);
        Assert.IsFalse(SchematicItemMerge.Plan(baseline.Data, competingXml, after.Data).CanApply);
        var keepNativePosition = new Dictionary<Guid, SchematicConflictChoice>
            { [Guid.Parse(conflictingMove.Id.Value)] = SchematicConflictChoice.Native };
        Assert.IsFalse(SchematicItemMerge.Resolve(baseline.Data, competingXml, after.Data, keepNativePosition).CanApply);
        var choice = SchematicItemMerge.Resolve(baseline.Data, competingXml, after.Data,
            keepNativePosition, variantChoices: new Dictionary<string, SchematicConflictChoice>
                { [usedVariant] = SchematicConflictChoice.Xml });
        Assert.IsTrue(choice.CanApply);
        Assert.AreEqual(1, choice.NativeOperations.Count);
        Assert.IsNotNull(choice.NativeOperations.Single().ReplaceVariantRegistry);
        var resolve = Batch(after); resolve.Operations.Add(choice.NativeOperations);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(resolve, token);
        Assert.AreEqual(choice.Merged, (await Read()).Data); await Persisted(choice.Merged!);
        await UndoRedo("z", expectedAfter);
        await UndoRedo("y", choice.Merged!);
        await UndoRedo("z", expectedAfter);
        await UndoRedo("z", baseline.Data);
        await UndoRedo("y", expectedAfter);
        await UndoRedo("z", baseline.Data);
        await UndoRedo("z", initial.Data);
    }
}
