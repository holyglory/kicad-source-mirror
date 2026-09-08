using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyIndependentViews(NativeClient client, DocumentSpecifier document,
        int processId, string evidence, CancellationToken token)
    {
        Task<SchematicObservation> Observe() => client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
            new() { Document = document }, token);
        Task<SchematicViewSet> Render(RenderSchematicViews request) =>
            client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token);
        var humanBefore = await Observe();
        var dirtyBefore = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);
        var text = humanBefore.Snapshot.Data.Items.First(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        var page = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
            new() { Document = document }, token);
        var request = new RenderSchematicViews { Document = document };
        request.Views.Add(new SchematicRenderView { Key = "overview", WidthPixels = 800, HeightPixels = 600,
            Region = page.PageBounds.Clone() });
        request.Views.Add(new SchematicRenderView { Key = "detail", WidthPixels = 512, HeightPixels = 512,
            Region = new() { Position = new() { XNm = text.Text.Position.XNm - 8000000,
                YNm = text.Text.Position.YNm - 8000000 }, Size = new() { XNm = 16000000, YNm = 16000000 } } });
        var first = await Render(request);
        Assert.AreEqual(humanBefore.Snapshot, first.Snapshot);
        Assert.IsNotEmpty(first.Limitations);
        Assert.AreEqual(2, first.Views.Count);
        for (int index = 0; index < first.Views.Count; index++)
        {
            var wanted = request.Views[index]; var actual = first.Views[index]; var image = actual.Preview;
            Assert.AreEqual(wanted.Key, actual.Key);
            Assert.AreEqual(document, image.Document);
            Assert.AreEqual(first.Snapshot.Revision, image.Revision);
            Assert.IsFalse(image.TrackingComplete);
            Assert.AreEqual(wanted.WidthPixels, image.WidthPixels);
            Assert.AreEqual(wanted.HeightPixels, image.HeightPixels);
            Assert.AreEqual(image.WidthPixels, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.Png.Span.Slice(16, 4)));
            Assert.AreEqual(image.HeightPixels, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.Png.Span.Slice(20, 4)));
            double scale = Math.Max((double)wanted.Region.Size.XNm / wanted.WidthPixels,
                (double)wanted.Region.Size.YNm / wanted.HeightPixels);
            Assert.AreEqual(scale, image.Viewport.PixelXDxNm, 0.01);
            Assert.AreEqual(scale, image.Viewport.PixelYDyNm, 0.01);
            Assert.AreEqual(0, image.Viewport.PixelXDyNm, 0.01);
            Assert.AreEqual(0, image.Viewport.PixelYDxNm, 0.01);
            Assert.AreEqual(wanted.Region.Position.XNm + wanted.Region.Size.XNm / 2.0,
                image.Viewport.OriginXNm + image.WidthPixels * scale / 2, 0.01);
            Assert.AreEqual(wanted.Region.Position.YNm + wanted.Region.Size.YNm / 2.0,
                image.Viewport.OriginYNm + image.HeightPixels * scale / 2, 0.01);
            await File.WriteAllBytesAsync(Path.Combine(evidence, $"{processId}-independent-{actual.Key}.png"), image.Png.ToByteArray(), token);
        }
        Assert.AreEqual(first, await Render(request), "Unchanged native views must be deterministic.");
        var layers = new RenderSchematicViews { Document = document };
        foreach (string name in new[] { "notes", "wires", "drawing_sheet", "page_boundary" })
        {
            var view = request.Views[name is "notes" or "wires" ? 1 : 0].Clone(); view.Key = name;
            view.NativeLayers.Add(first.AvailableLayers.Single(l => l.Name == name).Id);
            layers.Views.Add(view);
        }
        var separated = await Render(layers);
        Assert.AreEqual(first.Snapshot, separated.Snapshot);
        Assert.AreNotEqual(separated.Views[0].Preview.Png, separated.Views[1].Preview.Png,
            "A region containing the fixture text must not render identically with notes excluded.");
        Assert.AreNotEqual(separated.Views[2].Preview.Png, separated.Views[3].Preview.Png,
            "The native title block and frame must differ from a bare page outline.");
        for (int index = 0; index < separated.Views.Count; index++)
        {
            var image = separated.Views[index].Preview;
            CollectionAssert.AreEqual(layers.Views[index].NativeLayers.ToArray(), image.Viewport.VisibleNativeLayers.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(evidence, $"{processId}-independent-{separated.Views[index].Key}.png"),
                image.Png.ToByteArray(), token);
        }
        async Task Invalid(Action<RenderSchematicViews> change)
        {
            var invalid = request.Clone(); change(invalid);
            var error = await Assert.ThrowsExactlyAsync<NativeApiException>(() => Render(invalid));
            Assert.AreEqual(3, error.Status);
        }
        await Invalid(r => r.Views[0].WidthPixels = 0);
        await Invalid(r => r.Views[0].Region.Size.XNm = -1);
        await Invalid(r => r.Views[0].Region.Position.XNm = long.MaxValue);
        await Invalid(r => r.Views[1].Key = r.Views[0].Key);
        await Invalid(r => r.Views[0].NativeLayers.Add(int.MaxValue));
        await Invalid(r => r.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D"));
        Assert.AreEqual(first, await Render(request), "A rejected view request must allow recovery.");
        var humanAfter = await Observe();
        Assert.AreEqual(humanBefore, humanAfter,
            "Independent renders must preserve the human canvas image, viewport, sheet and supported design state.");
        Assert.AreEqual(dirtyBefore, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token));
    }
}
