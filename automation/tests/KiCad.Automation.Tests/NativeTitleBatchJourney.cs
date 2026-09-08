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
    private static async Task VerifyTitleBatch(NativeClient client, DocumentSpecifier document,
        int processId, string display, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read(CancellationToken? local = null) =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, local ?? token);
        var original = await Read();
        var desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(original.Data));
        desired.Metadata.Page = new() { PageSize = (PageSize)4, Orientation = (PageOrientation)2 };
        desired.Metadata.TitleBlock = new()
        {
            Title = "Controller — model update", Date = "2026-09-06", Revision = "A.2", Company = "Fixture",
            Comment1 = "one", Comment2 = "two", Comment3 = "three", Comment4 = "four", Comment5 = "five",
            Comment6 = "six", Comment7 = "seven", Comment8 = "eight", Comment9 = "nine"
        };
        var oldNote = desired.Items.First(i => i.Is(SchematicText.Descriptor));
        var note = oldNote.Unpack<SchematicText>(); note.Text.Position.XNm += 2540000;
        desired.Items[desired.Items.IndexOf(oldNote)] = Any.Pack(note);
        ApplySchematicItemBatch Plan(SchematicScreenDataSnapshot current, SchematicScreenData target)
        {
            var batch = new ApplySchematicItemBatch { Document = document, Description = "Model title and note",
                DocumentEpoch = current.Revision.Epoch, ExpectedRevision = current.Revision.Clone(),
                OperationId = Guid.NewGuid().ToString("D") };
            batch.Operations.Add(SchematicItemDelta.Plan(current.Data, target)); return batch;
        }
        Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var failed = Plan(original, desired);
        Assert.AreEqual(3, failed.Operations.Count);
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(original, await Read(), "Failed mixed batch must restore title, items and revision.");
        var change = Plan(original, desired);
        var applied = await Apply(change);
        Assert.IsTrue(applied.TitleBlockChanged); Assert.IsTrue(applied.PageSettingsChanged);
        var changed = await Read(); Assert.AreEqual(desired, changed.Data);
        Assert.IsTrue((await Apply(change)).TitleBlockChanged);
        Assert.AreEqual(changed, await Read(), "Retry must not add a second edit.");
        var noOp = new ApplySchematicItemBatch { Document = document };
        noOp.Operations.Add(new SchematicItemOperation { SetTitleBlock = desired.Metadata.TitleBlock.Clone() });
        Assert.IsFalse((await Apply(noOp)).TitleBlockChanged); Assert.AreEqual(changed, await Read());
        var invalid = Plan(changed, original.Data);
        invalid.Operations.Add(new SchematicItemOperation { SetTitleBlock = new() { Comment9 = "before\0after" } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(invalid));
        Assert.AreEqual(changed, await Read(), "Invalid text must not truncate or leave earlier operations applied.");
        var stale = Plan(original, original.Data); // Explicit stale cursor even with a real operation.
        stale.Operations.Add(new SchematicItemOperation { SetTitleBlock = original.Data.Metadata.TitleBlock.Clone() });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.AreEqual(changed, await Read());
        foreach (var (key, expected) in new[] { ("z", original.Data), ("y", desired) })
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot observed;
            do
            {
                observed = await Read(deadline.Token);
                if (observed.Revision.Equals(before.Revision)) await Task.Delay(100, deadline.Token);
            } while (observed.Revision.Equals(before.Revision));
            Assert.AreEqual(expected, observed.Data, "One native undo/redo must restore both title and note.");
            var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, token);
            Assert.AreEqual(observed, observation.Snapshot);
            Assert.AreEqual(observed.Revision, observation.Preview.Revision);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read(); Assert.AreEqual(desired.Metadata.TitleBlock, reopened.Data.Metadata.TitleBlock);
        Assert.AreEqual(desired.Metadata.Page, reopened.Data.Metadata.Page);
        var cleared = reopened.Data.Clone(); cleared.Metadata.TitleBlock = new();
        Assert.IsTrue((await Apply(Plan(reopened, cleared))).TitleBlockChanged);
        reopened = await Read(); Assert.AreEqual(new TitleBlockInfo(), reopened.Data.Metadata.TitleBlock);
        // Loading may legitimately normalize unrelated persisted objects; restore
        // only the title and edited note in this fixture's fresh observed state.
        var restore = reopened.Data.Clone(); restore.Metadata.TitleBlock = original.Data.Metadata.TitleBlock.Clone();
        restore.Metadata.Page = original.Data.Metadata.Page.Clone();
        var freshNote = restore.Items.Single(i => i.Is(SchematicText.Descriptor) && i.Unpack<SchematicText>().Id.Equals(note.Id));
        restore.Items[restore.Items.IndexOf(freshNote)] = oldNote.Clone();
        Assert.IsTrue((await Apply(Plan(reopened, restore))).TitleBlockChanged);
        Assert.AreEqual(restore, (await Read()).Data);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
    }
}
