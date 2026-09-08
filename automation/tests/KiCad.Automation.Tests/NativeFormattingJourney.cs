using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyFormatting(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read() =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        ApplySchematicItemBatch Batch(SchematicScreenDataSnapshot before, SchematicFormattingSettings settings)
        {
            var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = before.Revision.Clone(),
                DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"),
                Description = "Project formatting XML round trip" };
            batch.Operations.Add(new SchematicItemOperation { SetFormatting = settings.Clone() });
            return batch;
        }
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        async Task Observe(SchematicScreenDataSnapshot expected, string phase)
        {
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
            Assert.AreEqual(expected, observation.Snapshot);
            Assert.AreEqual(expected.Revision, observation.Preview.Revision);
            await File.WriteAllBytesAsync(Path.Combine(evidence, processId + "-formatting-" + phase + ".png"),
                observation.Preview.Png.ToByteArray(), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, processId + "-formatting-" + phase + ".xml"),
                SchematicDataXml.Write(expected.Data), token);
        }
        var original = await Read();
        Assert.IsNotNull(original.Data.Metadata.Formatting);
        Assert.IsFalse(original.Data.Metadata.Formatting.ShowIntersheetReferences,
            "The isolated fixture starts with reference visibility off so enabling it exercises native field placement.");
        var label = new GlobalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = new() { XNm = 65000000, YNm = 65000000 },
            Shape = SchematicLabelShape.SlshBidi, SpinStyle = SchematicLabelSpinStyle.SlssRight,
            Text = new() { Text_ = "FORMATTING_TEST", Position = new() { XNm = 65000000, YNm = 65000000 },
                Attributes = new() { Size = new() { XNm = 1270000, YNm = 1270000 },
                    HorizontalAlignment = HorizontalAlignment.HaLeft, VerticalAlignment = VerticalAlignment.VaCenter } } };
        label.IntersheetRefsField = new SchematicField
        {
            Name = "Intersheetrefs", AllowAutoPlace = true, Visible = false,
            Text = label.Text.Clone()
        };
        label.IntersheetRefsField.Text.Text_ = "${INTERSHEET_REFS}";
        // SCH_FIELD reloads with the native multiline text mode, unlike label text.
        label.IntersheetRefsField.Text.Attributes.Multiline = true;
        var setup = Batch(original, original.Data.Metadata.Formatting);
        setup.Operations.Add(new SchematicItemOperation { Create = Any.Pack(label) });
        await Apply(setup);
        var before = await Read();
        var originalLabel = before.Data.Items.Single(i => i.Is(GlobalLabel.Descriptor)
            && i.Unpack<GlobalLabel>().Id.Equals(label.Id)).Unpack<GlobalLabel>();
        Assert.IsNotNull(originalLabel.IntersheetRefsField,
            "The field-placement fixture must retain its explicit reference field.");
        Assert.IsFalse(originalLabel.IntersheetRefsField.Visible);
        Assert.AreEqual(originalLabel.Position, originalLabel.IntersheetRefsField.Text.Position);
        Assert.IsNotNull(before.Data.Metadata.Formatting);
        await Observe(before, "before");
        var invalidField = originalLabel.Clone();
        invalidField.IntersheetRefsField.Text.Attributes.Multiline = false;
        var rejectedSettings = before.Data.Metadata.Formatting.Clone();
        rejectedSettings.ShowDnpMarkers = !rejectedSettings.ShowDnpMarkers;
        var rejectedField = Batch(before, rejectedSettings);
        rejectedField.Operations.Add(new SchematicItemOperation { Update = Any.Pack(invalidField) });
        var fieldError = await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(rejectedField));
        StringAssert.Contains(fieldError.Message, "multiline");
        Assert.AreEqual(before, await Read(), "An unsavable field must roll back the preceding formatting edit too.");
        var desired = before.Data.Metadata.Formatting.Clone();
        desired.DefaultLineWidthNm = 254000; desired.DefaultTextSizeNm = 1524000;
        desired.PinSymbolSizeNm = 762000; desired.ConnectionGridNm = 635000;
        desired.JunctionSizeChoice = 5; desired.HopOverSizeChoice = 4;
        desired.ShowDnpMarkers = !desired.ShowDnpMarkers;
        desired.ShowIntersheetReferences = !desired.ShowIntersheetReferences;
        desired.ListOwnPage = false; desired.ShortReferenceFormat = true;
        desired.ReferencePrefix = "頁〈"; desired.ReferenceSuffix = "〉";
        desired.OperatingPoint.VoltagePrecision = 10; desired.OperatingPoint.CurrentPrecision = 1;
        desired.OperatingPoint.VoltageRange = "mV"; desired.OperatingPoint.CurrentRange = "uA";
        desired.UnitReference.SeparatorAscii = '.'; desired.UnitReference.FirstIdAscii = '1';

        foreach (Action<SchematicFormattingSettings> corrupt in new Action<SchematicFormattingSettings>[]
        {
            s => s.OperatingPoint = null, s => s.OperatingPoint.VoltagePrecision = 0,
            s => s.OperatingPoint.CurrentPrecision = 11, s => s.OperatingPoint.VoltageRange = "mA",
            s => s.OperatingPoint.CurrentRange = "kA", s => s.UnitReference = null,
            s => s.UnitReference.SeparatorAscii = 127, s => s.UnitReference.FirstIdAscii = 48,
            s => s.UnitReference.FirstIdAscii = 123
        })
        {
            var malformed = desired.Clone(); corrupt(malformed);
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(before, malformed)));
            Assert.AreEqual(before, await Read(), "Rejected overlay formatting must leave the entire snapshot unchanged.");
        }

        var invalid = Batch(before, desired); invalid.Operations[0].SetFormatting.ConnectionGridNm += 1;
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(invalid));
        Assert.AreEqual(before, await Read());
        var failed = Batch(before, desired);
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(before, await Read(), "Rollback must include reference fields automatically positioned by the native setting change.");
        var batch = Batch(before, desired);
        var result = await Apply(batch);
        Assert.IsTrue(result.FormattingChanged);
        var changed = await Read();
        Assert.AreEqual(desired, changed.Data.Metadata.Formatting);
        var changedLabel = changed.Data.Items.Single(i => i.Is(GlobalLabel.Descriptor)
            && i.Unpack<GlobalLabel>().Id.Equals(label.Id)).Unpack<GlobalLabel>();
        Assert.IsTrue(changedLabel.IntersheetRefsField.Visible);
        Assert.AreNotEqual(originalLabel.IntersheetRefsField.Text.Position, changedLabel.IntersheetRefsField.Text.Position,
            "The fixture must exercise real native field placement, not only a metadata edit.");
        Assert.AreEqual(changed.Data, SchematicDataXml.Read(SchematicDataXml.Write(changed.Data)));
        await Observe(changed, "changed");
        Assert.AreEqual(result, await Apply(batch));
        Assert.AreEqual(changed, await Read());
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.IsFalse((await Apply(Batch(changed, desired))).FormattingChanged);
        Assert.AreEqual(changed, await Read());
        foreach (var (key, expected, phase) in new[] { ("z", before.Data, "undo"), ("y", changed.Data, "redo") })
        {
            var previous = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot actual;
            do
            {
                actual = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, deadline.Token);
                if (actual.Revision.Equals(previous.Revision)) await Task.Delay(100, deadline.Token);
            } while (actual.Revision.Equals(previous.Revision));
            Assert.AreEqual(expected, actual.Data, "Formatting undo/redo must restore exact field positions and visibility.");
            await Observe(actual, phase);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read();
        await Observe(reopened, "reopened");
        Assert.IsTrue(changed.Data.Equals(reopened.Data), NativeSnapshotDifference.Describe(changed.Data, reopened.Data));
        var restore = new ApplySchematicItemBatch { Document = document, ExpectedRevision = reopened.Revision,
            DocumentEpoch = reopened.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Restore formatting fixture" };
        restore.Operations.Add(SchematicItemDelta.Plan(reopened.Data, before.Data));
        await Apply(restore);
        Assert.AreEqual(before.Data, (await Read()).Data);
        var cleanup = Batch(await Read(), original.Data.Metadata.Formatting);
        cleanup.Operations.Add(new SchematicItemOperation { Remove = label.Id.Clone() });
        await Apply(cleanup);
        Assert.AreEqual(original.Data, (await Read()).Data);
    }
}
