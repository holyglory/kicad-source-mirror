using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMultiUnitReferenceDisplay(NativeClient client, DocumentSpecifier document,
        SchematicSymbolInstance first, SchematicSymbolInstance second, int processId, string display,
        string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        var original = await Read();
        Assert.AreEqual(0u, original.Data.Metadata.Formatting.UnitReference.SeparatorAscii);
        Assert.AreEqual((uint)'A', original.Data.Metadata.Formatting.UnitReference.FirstIdAscii);
        await Observe(original, "A", "B", "before");
        var desired = original.Data.Clone();
        desired.Metadata.Formatting.UnitReference.SeparatorAscii = '.';
        desired.Metadata.Formatting.UnitReference.FirstIdAscii = '1';
        desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
        var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = original.Revision.Clone(),
            DocumentEpoch = original.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"),
            Description = "Change displayed multi-unit references through XML" };
        batch.Operations.Add(SchematicItemDelta.Plan(original.Data, desired));
        Assert.AreEqual(1, batch.Operations.Count);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var changed = await Read();
        Assert.AreEqual(desired, changed.Data, "A display preference must not change component or pin identities.");
        await Observe(changed, ".1", ".2", "numeric");

        NativeKeyboard.SchematicShortcut(display, processId, "z");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        SchematicScreenDataSnapshot restored;
        do
        {
            restored = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, deadline.Token);
            if (restored.Revision.Equals(changed.Revision)) await Task.Delay(100, deadline.Token);
        } while (restored.Revision.Equals(changed.Revision));
        Assert.AreEqual(original.Data, restored.Data);
        await Observe(restored, "A", "B", "undo");

        async Task Observe(SchematicScreenDataSnapshot expected, string firstSuffix, string secondSuffix, string phase)
        {
            var facts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
                new() { Document = document }, token);
            Assert.AreEqual(expected.Revision, facts.Revision);
            var detailRequest = new RenderSchematicViews { Document = document };
            foreach (var (symbol, suffix) in new[] { (first, firstSuffix), (second, secondSuffix) })
            {
                var reference = facts.Objects.Single(o => o.OwnerId is not null && o.OwnerId.Equals(symbol.Id)
                    && o.Kind == SchematicPresentationObject.Types.Kind.ReferenceDesignator);
                Assert.IsTrue(reference.Visible);
                Assert.AreEqual("U1" + suffix, reference.Text,
                    "Native displayed text must use the selected unit and current project formatting.");
                var region = reference.Bounds.Clone();
                region.Position.XNm -= 2000000; region.Position.YNm -= 2000000;
                region.Size.XNm += 4000000; region.Size.YNm += 4000000;
                detailRequest.Views.Add(new SchematicRenderView { Key = "unit-" + symbol.Unit.Unit,
                    Region = region, WidthPixels = 640, HeightPixels = 320 });
            }
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token);
            Assert.AreEqual(expected, observation.Snapshot);
            Assert.AreEqual(facts.Revision, observation.Preview.Revision);
            var details = await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(detailRequest, token);
            var firstRegion = detailRequest.Views[0].Region;
            var secondRegion = detailRequest.Views[1].Region;
            Assert.IsTrue(firstRegion.Position.YNm + firstRegion.Size.YNm < secondRegion.Position.YNm,
                "The two displayed-unit fixtures must occupy separate close-ups, not overlapping text.");
            Assert.AreEqual(expected, details.Snapshot);
            Assert.AreEqual(2, details.Views.Count);
            foreach (var view in details.Views)
            {
                Assert.AreEqual(facts.Revision, view.Preview.Revision);
                await File.WriteAllBytesAsync(Path.Combine(evidence,
                    $"{processId}-unit-reference-{phase}-{view.Key}.png"), view.Preview.Png.ToByteArray(), token);
            }
            Assert.AreEqual(observation, await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token), "Close-ups must not navigate or alter the human canvas.");
            await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-unit-reference-" + phase + ".png"),
                observation.Preview.Png.ToByteArray(), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, processId + "-unit-reference-" + phase + ".xml"),
                SchematicDataXml.Write(expected.Data), token);
        }
    }
}
