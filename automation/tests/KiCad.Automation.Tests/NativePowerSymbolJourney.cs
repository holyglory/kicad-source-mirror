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
    private static async Task VerifyPowerSymbolXml(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, string evidence, string instanceId, CancellationToken token)
    {
        var query = new GetItemsById { Header = new ItemHeader { Document = document } };
        query.Items.Add(new KIID { Value = fixture.Symbol });
        async Task<SchematicSymbolInstance> Read() =>
            (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicSymbolInstance>();
        async Task<string[]> Nets() =>
            (await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(
                new() { Document = document }, token)).Nets.Select(n => n.Name + ":" +
                string.Join(";", n.Sheets.Select(s => s.Path.ToString() + ":" +
                    string.Join(",", s.Items.Select(i => i.Value).Order(StringComparer.Ordinal)))
                    .Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToArray();
        async Task Apply(SchematicItemOperation operation)
        {
            var batch = new ApplySchematicItemBatch { Document = document, Description = "Power symbol XML fidelity" };
            batch.Operations.Add(operation);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }

        var original = await Read();
        var originalNets = await Nets();
        var documentAssets = (await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
            new() { Document = document }, token)).Metadata.EmbeddedFiles;
        Assert.IsTrue(documentAssets.Files.Count > 0, "The fixture must provide a real encoded asset.");
        // Exercise the native editor, not merely protobuf equality: power type
        // and library defaults must survive removal and creation from typed XML.
        foreach (var type in new[] { SchematicSymbolType.SstGlobalPower, SchematicSymbolType.SstLocalPower })
        {
            var power = original.Clone();
            power.Definition.Type = type;
            power.Attributes.DoNotPopulate = true;
            power.Definition.EmbeddedFiles = documentAssets.Clone();
            foreach (var asset in power.Definition.EmbeddedFiles.Files)
                asset.Name = "library-" + asset.Name;
            foreach (var child in power.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                pin.ElectricalType = ElectricalPinType.EptPowerInput;
                child.Item = Any.Pack(pin);
            }
            power.Definition.Attributes = new SchematicSymbolAttributes
            {
                ExcludeFromSimulation = true,
                ExcludeFromBillOfMaterials = true,
                ExcludeFromBoard = true,
                ExcludeFromPositionFiles = true
            };
            await Apply(new SchematicItemOperation { Update = Any.Pack(power) });
            var observed = await Read();
            Assert.AreEqual(type, observed.Definition.Type);
            Assert.AreEqual(power.Definition.Attributes, observed.Definition.Attributes);
            Assert.AreEqual(power.Definition.EmbeddedFiles, observed.Definition.EmbeddedFiles);
            Assert.AreEqual(power.Attributes, observed.Attributes,
                "Library defaults must not overwrite the placed component's explicit attributes.");
            var baseline = await Nets();
            var beforeRejected = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                new() { Document = document }, token);
            var invalid = observed.Clone();
            invalid.Position.XNm += 2540000;
            invalid.Definition.Items.Add(new SchematicSymbolChild
            {
                Item = new Any { TypeUrl = "type.googleapis.com/future.SymbolGraphic" }
            });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                Apply(new SchematicItemOperation { Update = Any.Pack(invalid) }));
            Assert.AreEqual(observed, await Read(), "Unsupported children must not be dropped during an otherwise successful move.");
            CollectionAssert.AreEqual(baseline, await Nets());
            var afterRejected = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                new() { Document = document }, token);
            Assert.AreEqual(beforeRejected.Sequence, afterRejected.Sequence, "Rejected input must not commit an edit.");
            var invalidDefault = observed.Clone();
            invalidDefault.Definition.Attributes.DoNotPopulate = true;
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                Apply(new SchematicItemOperation { Update = Any.Pack(invalidDefault) }));
            Assert.AreEqual(observed, await Read(), "DNP is a supported instance setting, not a saved library default.");
            var corruptAsset = observed.Clone();
            corruptAsset.Position.XNm += 2540000;
            corruptAsset.Definition.EmbeddedFiles.Files[0].DataHash = "invalid checksum";
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                Apply(new SchematicItemOperation { Update = Any.Pack(corruptAsset) }));
            Assert.AreEqual(observed, await Read());
            CollectionAssert.AreEqual(baseline, await Nets());
            string xml = SchematicDataXml.Write(observed);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-" + type + ".xml"), xml, token);
            await Apply(new SchematicItemOperation { Remove = observed.Id.Clone() });
            var removedNets = await Nets();
            Assert.IsFalse(baseline.SequenceEqual(removedNets), "Removing the connected symbol must affect the graph.");
            await Apply(new SchematicItemOperation { Create = Any.Pack(SchematicDataXml.Read(xml)) });
            Assert.AreEqual(observed, await Read(), "Power semantics must survive native creation from XML.");
            CollectionAssert.AreEqual(baseline, await Nets(), "Power symbol restoration must preserve exact pin connectivity.");
            var image = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                new() { Document = document }, token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-" + type + ".png"), image.Png.ToByteArray(), token);
            await Apply(new SchematicItemOperation { Update = Any.Pack(original) });
            Assert.AreEqual(original, await Read());
            CollectionAssert.AreEqual(originalNets, await Nets());
            Assert.AreEqual(documentAssets, (await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
                new() { Document = document }, token)).Metadata.EmbeddedFiles,
                "Library-owned assets must not replace or append document-owned attachments.");
        }
    }
}
