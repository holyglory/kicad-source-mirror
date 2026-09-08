using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyDrawingRatios(NativeClient client, DocumentSpecifier document,
        string file, int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot state) => new()
        {
            Document = document, DocumentEpoch = state.Revision.Epoch, ExpectedRevision = state.Revision,
            OperationId = Guid.NewGuid().ToString("D"), Description = "Drawing-ratio XML fixture"
        };
        var original = await Read();
        var line = new SchematicLine { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Start = new() { XNm = 30000000, YNm = 40000000 }, End = new() { XNm = 90000000, YNm = 40000000 },
            Type = (SchematicLineType)3, Stroke = new() { Width = new() { ValueNm = 500000 }, Style = (StrokeLineStyle)3 } };
        var unsupported = Batch(original);
        unsupported.Operations.Add(new SchematicItemOperation { Create = Any.Pack(new SchematicGraphicShape
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Shape = new() { Segment = new() { Start = line.Start.Clone(), End = line.End.Clone() } }
        }) });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(unsupported, token));
        Assert.AreEqual(original, await Read());
        var setup = Batch(original); setup.Operations.Add(new SchematicItemOperation { Create = Any.Pack(line) });
        var overbar = original.Data.Items.First(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        overbar.Id.Value = Guid.NewGuid().ToString("D");
        overbar.Text.Text_ = "~{CLOCK}";
        overbar.Text.Position = new() { XNm = 45000000, YNm = 55000000 };
        setup.Operations.Add(new SchematicItemOperation { Create = Any.Pack(overbar) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(setup, token);
        var unpositioned = await Read();
        var placementFacts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(new() { Document = document }, token);
        var placed = unpositioned.Data.Items.Where(i => i.Is(SchematicText.Descriptor)).Select(i => i.Unpack<SchematicText>())
            .Single(t => t.Id.Equals(overbar.Id));
        placed.Text.Position.YNm += placementFacts.PageBounds.Position.YNm
            - placementFacts.Objects.Single(o => o.Id.Equals(overbar.Id)).Bounds.Position.YNm + 50000;
        var placement = Batch(unpositioned); placement.Operations.Add(new SchematicItemOperation { Update = Any.Pack(placed) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(placement, token);
        var baseline = await Read();
        var baselineFacts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(new() { Document = document }, token);
        var baselineBounds = baselineFacts.Objects.Single(o => o.Id.Equals(overbar.Id)).Bounds;
        Assert.IsNotNull(baselineBounds);
        await PageOverflow(false);
        var before = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        var desired = baseline.Data.Clone();
        desired.Metadata.DrawingRatios = new() { DashLengthRatio = 2, GapLengthRatio = 7,
            TextOffsetRatio = 0.25, LabelSizeRatio = 0.5, OverbarHeightRatio = 1.5 };
        desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = Batch(baseline); batch.Operations.Add(SchematicItemDelta.Plan(baseline.Data, desired));
        Assert.AreEqual(1, batch.Operations.Count);
        var applied = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.IsTrue(applied.DrawingRatiosChanged);
        var after = await Read(); Assert.AreEqual(desired, after.Data);
        var updatedFacts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(new() { Document = document }, token);
        Assert.AreNotEqual(baselineBounds, updatedFacts.Objects.Single(o => o.Id.Equals(overbar.Id)).Bounds,
            "The native text extent query must reflect changed overbar metrics, not a stale cached bound.");
        await PageOverflow(true);
        var preview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        Assert.IsFalse(before.Png.Span.SequenceEqual(preview.Png.Span), "Changing the visible dashed-line ratio must change native rendering.");
        await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-drawing-ratios-before.png"), before.Png.ToByteArray(), token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-drawing-ratios-after.png"), preview.Png.ToByteArray(), token);
        Assert.AreEqual(applied, await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token));
        Assert.AreEqual(after, await Read());
        var noop = Batch(after); noop.Operations.Add(batch.Operations.Single().Clone());
        Assert.IsFalse((await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(noop, token)).DrawingRatiosChanged);
        Assert.AreEqual(after, await Read());
        var failed = Batch(after);
        failed.Operations.Add(new SchematicItemOperation { SetDrawingRatios = baseline.Data.Metadata.DrawingRatios.Clone() });
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failed, token));
        Assert.AreEqual(after, await Read());
        var invalid = Batch(after);
        invalid.Operations.Add(new SchematicItemOperation { SetDrawingRatios = new() { DashLengthRatio = -1 } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalid, token));
        Assert.AreEqual(after, await Read());
        await Persist(desired.Metadata.DrawingRatios);
        await UndoRedo("z", baseline.Data);
        await UndoRedo("y", desired);
        await UndoRedo("z", baseline.Data);
        await UndoRedo("z", unpositioned.Data);
        await UndoRedo("z", original.Data);

        async Task PageOverflow(bool expected)
        {
            var check = await NativePresentationChecks.CheckAsync(client, document, new PresentationPolicy(1, 3), token);
            Assert.AreEqual(expected, check.Report.Findings.Any(f => f.Rule == "page_overflow"
                && f.ObjectIds.Contains(Guid.Parse(overbar.Id.Value))),
                "Only the changed rendered extent crossing the page must trigger this text's overflow finding.");
        }

        async Task Persist(SchematicDrawingRatios expected)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
            using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(file, ".kicad_pro"), token));
            var drawing = project.RootElement.GetProperty("schematic").GetProperty("drawing");
            Assert.AreEqual(expected.DashLengthRatio, drawing.GetProperty("dashed_lines_dash_length_ratio").GetDouble());
            Assert.AreEqual(expected.GapLengthRatio, drawing.GetProperty("dashed_lines_gap_length_ratio").GetDouble());
            Assert.AreEqual(expected.TextOffsetRatio, drawing.GetProperty("text_offset_ratio").GetDouble());
            Assert.AreEqual(expected.LabelSizeRatio, drawing.GetProperty("label_size_ratio").GetDouble());
            Assert.AreEqual(expected.OverbarHeightRatio, drawing.GetProperty("overbar_offset_ratio").GetDouble());
        }
        async Task UndoRedo(string key, SchematicScreenData expected)
        {
            var previous = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token))
                .Revision.Equals(previous.Revision)) await Task.Delay(100, deadline.Token);
            Assert.AreEqual(expected, (await Read()).Data);
            await PageOverflow(expected.Metadata.DrawingRatios.OverbarHeightRatio == 1.5);
            await Persist(expected.Metadata.DrawingRatios);
        }
    }
}
