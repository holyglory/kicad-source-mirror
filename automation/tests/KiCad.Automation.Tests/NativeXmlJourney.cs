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
    private static async Task VerifyNativeSymbolXml(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, string evidence, string instanceId, CancellationToken token)
    {
        var query = new GetItemsById { Header = new ItemHeader { Document = document } };
        query.Items.Add(new KIID { Value = fixture.Symbol });
        async Task<SchematicSymbolInstance> Symbol() =>
            (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicSymbolInstance>();
        async Task<string[]> Nets() =>
            (await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(
                new() { Document = document }, token)).Nets.Select(n => n.Name + ":" +
                string.Join(";", n.Sheets.Select(s => s.Path.ToString() + ":" +
                    string.Join(",", s.Items.Select(i => i.Value).Order(StringComparer.Ordinal)))
                    .Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToArray();

        async Task Update(SchematicSymbolInstance value)
        {
            var batch = new ApplySchematicItemBatch { Document = document, Description = "XML variant fidelity" };
            batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(value) });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }

        var beforeVariant = await Symbol();
        var cacheSnapshot = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        Assert.IsNotEmpty(cacheSnapshot.Data.CachedSymbols);
        Assert.AreEqual(cacheSnapshot.Data.CachedSymbols.Count,
            cacheSnapshot.Data.CachedSymbols.Select(c => c.CacheKey).Distinct(StringComparer.Ordinal).Count());
        foreach (var cached in cacheSnapshot.Data.CachedSymbols)
        {
            Assert.IsTrue(cached.Definition.PinsUseLocalCoordinates);
            Assert.IsNotNull(cached.PinNameOffset);
            string cacheXml = SchematicDataXml.Write(cached);
            Assert.AreEqual(cached, SchematicDataXml.Read(cacheXml));
            foreach (var child in cached.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
                Assert.IsFalse(child.Item.Unpack<SchematicPin>().HasActiveAlternate);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-screen-with-cache.xml"),
            SchematicDataXml.Write(cacheSnapshot.Data), token);
        var withVariant = beforeVariant.Clone();
        withVariant.Variants ??= new SchematicSymbolVariants();
        var variant = new SchematicSymbolVariant
        {
            Name = "xml-reconstruction",
            Attributes = withVariant.Attributes.Clone(),
            SymbolOverride = (withVariant.LibraryId ?? withVariant.Definition.Id).Clone(),
            PinMapOverride = new PinMapInstanceOverride { Mode = PinMapOverrideMode.PmomForceIdentity }
        };
        variant.Fields.Add("Value", "Variant probe");
        variant.PinMapOverride.Edits.Add(new PinMapEntry { PinNumber = "1", PadNumber = "7" });
        withVariant.Variants.Variants.Add(variant);
        await Update(withVariant);
        var original = await Symbol();
        var observedVariant = original.Variants.Variants.Single(v => v.Name == variant.Name);
        Assert.AreEqual(variant.SymbolOverride, observedVariant.SymbolOverride);
        Assert.AreEqual(variant.PinMapOverride, observedVariant.PinMapOverride);

        // Omitted update fields preserve existing overrides, while explicitly
        // empty/default records clear them. A snapshot must express both cases.
        var omitted = original.Clone();
        var omittedVariant = omitted.Variants.Variants.Single(v => v.Name == variant.Name);
        omittedVariant.SymbolOverride = null;
        omittedVariant.PinMapOverride = null;
        await Update(omitted);
        Assert.AreEqual(original, await Symbol());
        var baseline = await Nets();
        string xml = SchematicDataXml.Write(original);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-symbol.xml"), xml, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-schematic-data.xsd"),
            SchematicDataXml.ExportSchema(), token);
        var remove = new ApplySchematicItemBatch { Document = document, Description = "XML reconstruction: remove probe" };
        remove.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = fixture.Symbol } });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
        var removedNets = await Nets();
        Assert.IsFalse(baseline.SequenceEqual(removedNets), "Removing the probe must change native connectivity.");

        var restored = (SchematicSymbolInstance)SchematicDataXml.Read(xml);
        var create = new ApplySchematicItemBatch { Document = document, Description = "XML reconstruction: restore probe" };
        create.Operations.Add(new SchematicItemOperation { Create = Any.Pack(restored) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(create, token);
        Assert.AreEqual(original, await Symbol(), "Native symbol properties and identities must survive XML reconstruction.");
        CollectionAssert.AreEqual(baseline, await Nets(), "XML reconstruction must restore exact native connectivity.");
        var cleared = restored.Clone();
        var clearedVariant = cleared.Variants.Variants.Single(v => v.Name == variant.Name);
        clearedVariant.SymbolOverride = new LibraryIdentifier();
        clearedVariant.PinMapOverride = new PinMapInstanceOverride { Mode = PinMapOverrideMode.PmomUseLibraryDefault };
        await Update(cleared);
        var afterClear = (await Symbol()).Variants.Variants.Single(v => v.Name == variant.Name);
        Assert.AreEqual(clearedVariant.SymbolOverride, afterClear.SymbolOverride);
        Assert.AreEqual(clearedVariant.PinMapOverride, afterClear.PinMapOverride);
        await Update(beforeVariant);
        Assert.AreEqual(beforeVariant, await Symbol());
        await VerifyPowerSymbolXml(client, document, fixture, evidence, instanceId, token);
        var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
            new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-xml-restored-symbol.png"),
            image.Png.ToByteArray(), token);
    }
}
