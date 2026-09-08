using Google.Protobuf;
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
    private static async Task VerifyHiddenSheetViews(NativeClient client, DocumentSpecifier root,
        DocumentSpecifier first, DocumentSpecifier second, Vector2 position, string evidence,
        string instanceId, CancellationToken token)
    {
        async Task Activate(DocumentSpecifier document) =>
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token);
        Text Text(string value, long dy) => new() { Text_ = value,
            Position = new() { XNm = position.XNm, YNm = position.YNm + dy },
            Attributes = new() { Size = new() { XNm = 1270000, YNm = 1270000 } } };
        var note = new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Text = Text("${SHEETPATH}|${#}/${##}", 5000000) };
        var label = new GlobalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Text = Text("${SHEETPATH}", 10000000), Shape = SchematicLabelShape.SlshBidi,
            SpinStyle = SchematicLabelSpinStyle.SlssRight };
        label.Text.Attributes.HorizontalAlignment = HorizontalAlignment.HaLeft;
        label.Text.Attributes.VerticalAlignment = VerticalAlignment.VaCenter;
        label.Position = label.Text.Position.Clone();
        async Task Apply(params SchematicItemOperation[] operations)
        {
            await Activate(first);
            var batch = new ApplySchematicItemBatch { Document = first, Description = "Sheet context rendering fixture" };
            batch.Operations.Add(operations);
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }
        async Task<SchematicViewSet> Render(DocumentSpecifier document, int? layer = null)
        {
            var request = new RenderSchematicViews { Document = document };
            var view = new SchematicRenderView { Key = "repeated-instance", WidthPixels = 800, HeightPixels = 600,
                Region = new() { Position = new() { XNm = position.XNm - 20000000, YNm = position.YNm - 20000000 },
                    Size = new() { XNm = 40000000, YNm = 40000000 } } };
            if (layer is int id) view.NativeLayers.Add(id);
            request.Views.Add(view);
            return await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token);
        }
        await Apply(new() { Create = Any.Pack(note) }, new() { Create = Any.Pack(label) });
        try
        {
            await Activate(root);
            var firstHidden = await Render(first);
            var secondHidden = await Render(second);
            Assert.AreEqual(first, firstHidden.Snapshot.Data.Metadata.Document);
            Assert.AreEqual(second, secondHidden.Snapshot.Data.Metadata.Document);
            int referenceLayer = firstHidden.AvailableLayers.Single(l => l.Name == "references").Id;
            var firstReference = await Render(first, referenceLayer);
            var secondReference = await Render(second, referenceLayer);
            Assert.AreNotEqual(firstReference.Views.Single().Preview.Png, secondReference.Views.Single().Preview.Png,
                "Distinct reference designators on one shared screen must render for their requested instances.");
            int notesLayer = firstHidden.AvailableLayers.Single(l => l.Name == "notes").Id;
            int labelsLayer = firstHidden.AvailableLayers.Single(l => l.Name == "global_labels").Id;
            Assert.AreNotEqual((await Render(first, notesLayer)).Views.Single().Preview.Png,
                (await Render(second, notesLayer)).Views.Single().Preview.Png,
                "Sheet variables in ordinary text must use the requested path.");
            Assert.AreNotEqual((await Render(first, labelsLayer)).Views.Single().Preview.Png,
                (await Render(second, labelsLayer)).Views.Single().Preview.Png,
                "Global-label text and flags must use the requested path.");
            foreach (var visible in new[] { first, second, root })
            {
                await Activate(visible);
                var before = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                    new() { Document = visible }, token);
                var dirty = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = visible }, token);
                Assert.AreEqual(firstHidden, await Render(first), "Requested-sheet output must not depend on the visible sheet.");
                Assert.AreEqual(secondHidden, await Render(second), "Repeated-sheet output must not depend on the visible sheet.");
                Assert.AreEqual(firstReference, await Render(first, referenceLayer));
                Assert.AreEqual(secondReference, await Render(second, referenceLayer));
                Assert.AreEqual(before, await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                    new() { Document = visible }, token), "Rendering other sheets must preserve the human view and design.");
                Assert.AreEqual(dirty, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = visible }, token));
            }
            foreach (var (name, image) in new[] { ("first-references", firstReference), ("second-references", secondReference),
                ("first-artwork", firstHidden), ("second-artwork", secondHidden) })
                await File.WriteAllBytesAsync(Path.Combine(evidence, $"{instanceId}-hidden-{name}.png"),
                    image.Views.Single().Preview.Png.ToByteArray(), token);
        }
        finally
        {
            try { await Apply(new() { Remove = note.Id }, new() { Remove = label.Id }); }
            finally { await Activate(root); }
        }
    }
}
