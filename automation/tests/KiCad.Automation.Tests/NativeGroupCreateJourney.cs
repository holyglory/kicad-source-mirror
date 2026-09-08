using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Common.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyGroupCreation(NativeClient client, DocumentSpecifier document,
        int processId, string display, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Read(CancellationToken? local = null) =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, local ?? token);
        var baseline = await Read(); var desired = baseline.Data.Clone();
        var note = desired.Items.First(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        var existingMember = note.Id.Clone();
        note.Id.Value = Guid.NewGuid().ToString("D"); note.Text.Text_ = "New grouped instruction";
        note.Text.Position.YNm += 30000000;
        var inner = new Group { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Name = "New inner", Locked = (LockedState)1, DesignBlockLibraryId = "FixtureBlocks:New" };
        inner.Items.Add(note.Id.Clone());
        inner.Items.Add(existingMember);
        var sortedMembers = inner.Items.OrderBy(member => member.Value, StringComparer.Ordinal).ToArray();
        inner.Items.Clear(); inner.Items.Add(sortedMembers);
        var outer = new Group { Id = new() { Value = Guid.NewGuid().ToString("D") }, Name = "New outer", Locked = (LockedState)1 };
        outer.Items.Add(inner.Id.Clone());
        desired.Items.Add(Any.Pack(outer)); desired.Items.Add(Any.Pack(note)); desired.Items.Add(Any.Pack(inner));
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Create model groups",
            ExpectedRevision = baseline.Revision.Clone(), DocumentEpoch = baseline.Revision.Epoch,
            OperationId = Guid.NewGuid().ToString("D") };
        batch.Operations.Add(SchematicItemDelta.Plan(baseline.Data, desired));
        Assert.AreEqual(3, batch.Operations.Count);
        var failed = batch.Clone(); failed.OperationId = Guid.NewGuid().ToString("D");
        failed.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failed, token));
        Assert.AreEqual(baseline, await Read(), "Failed group creation must not leave members or parent pointers behind.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var changed = await Read();
        Assert.AreEqual(0, SchematicItemDelta.Plan(changed.Data, desired).Count);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(changed, await Read());
        var reparented = changed.Data.Clone();
        var revisedInner = inner.Clone(); revisedInner.Items.Remove(existingMember);
        var revisedOuter = outer.Clone(); revisedOuter.Items.Add(existingMember.Clone());
        var outerMembers = revisedOuter.Items.OrderBy(i => i.Value, StringComparer.Ordinal).ToArray();
        revisedOuter.Items.Clear(); revisedOuter.Items.Add(outerMembers);
        reparented.Items[reparented.Items.IndexOf(Any.Pack(inner))] = Any.Pack(revisedInner);
        reparented.Items[reparented.Items.IndexOf(Any.Pack(outer))] = Any.Pack(revisedOuter);
        var regroup = new ApplySchematicItemBatch { Document = document, ExpectedRevision = changed.Revision,
            DocumentEpoch = changed.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        regroup.Operations.Add(SchematicItemDelta.Plan(changed.Data, reparented));
        var lockedRegroup = regroup.Clone(); lockedRegroup.OperationId = Guid.NewGuid().ToString("D");
        var lockedMember = changed.Data.Items.Where(i => i.Is(SchematicText.Descriptor))
            .Select(i => i.Unpack<SchematicText>()).Single(i => i.Id.Equals(existingMember)).Clone();
        lockedMember.Locked = (LockedState)2;
        lockedRegroup.Operations.Insert(0, new SchematicItemOperation { Update = Any.Pack(lockedMember) });
        var lockedError = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(lockedRegroup, token));
        StringAssert.Contains(lockedError.Message, "locked");
        Assert.AreEqual(changed, await Read(), "A rejected locked-member move must also roll back the preceding lock edit.");
        var failedRegroup = regroup.Clone(); failedRegroup.OperationId = Guid.NewGuid().ToString("D");
        failedRegroup.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failedRegroup, token));
        Assert.AreEqual(changed, await Read(), "Rejected reparenting must restore content and revision.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(regroup, token);
        Assert.IsTrue(reparented.Equals((await Read()).Data), "Reparenting must preserve every object and group identity.");
        var regrouped = await Read();
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(regroup, token);
        Assert.AreEqual(regrouped, await Read(), "A reparenting retry must not change state twice.");
        foreach (var (key, expected) in new[] { ("z", changed.Data), ("y", reparented), ("z", changed.Data) })
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot observed;
            do
            {
                observed = await Read(wait.Token);
                if (observed.Revision.Equals(before.Revision)) await Task.Delay(100, wait.Token);
            } while (observed.Revision.Equals(before.Revision));
            Assert.IsTrue(expected.Equals(observed.Data), "Undo/redo must restore group membership and preserve all members.");
        }
        await VerifyDeletedGroupTransfer(client, document, inner, outer, processId, display, token);
        changed = await Read();
        var ungrouped = changed.Data.Clone();
        foreach (var groupItem in ungrouped.Items.Where(i => i.Is(Group.Descriptor)
            && (i.Unpack<Group>().Id.Equals(inner.Id) || i.Unpack<Group>().Id.Equals(outer.Id))).ToArray())
            ungrouped.Items.Remove(groupItem);
        var remove = new ApplySchematicItemBatch { Document = document, ExpectedRevision = changed.Revision,
            DocumentEpoch = changed.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D"), Description = "Ungroup model objects" };
        remove.Operations.Add(SchematicItemDelta.Plan(changed.Data, ungrouped));
        Assert.AreEqual(2, remove.Operations.Count);
        var failedRemove = remove.Clone(); failedRemove.OperationId = Guid.NewGuid().ToString("D");
        failedRemove.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failedRemove, token));
        Assert.AreEqual(changed, await Read());
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
        var removedState = (await Read()).Data;
        Assert.AreEqual(ungrouped.Items.Count, removedState.Items.Count,
            "Remaining groups: " + string.Join(",", removedState.Items.Where(i => i.Is(Group.Descriptor)).Select(i => i.Unpack<Group>().Name)));
        Assert.AreEqual(ungrouped.Metadata, removedState.Metadata);
        Assert.IsTrue(ungrouped.Equals(removedState), "Changed object types: " + string.Join(",",
            removedState.Items.Where(i => !ungrouped.Items.Contains(i)).Select(i => i.TypeUrl)));
        foreach (var (key, expected) in new[] { ("z", changed.Data), ("y", ungrouped), ("z", changed.Data) })
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot observed;
            do
            {
                observed = await Read(wait.Token);
                if (observed.Revision.Equals(before.Revision)) await Task.Delay(100, wait.Token);
            } while (observed.Revision.Equals(before.Revision));
            Assert.AreEqual(expected, observed.Data, "Ungroup undo/redo must restore both nested membership and all member content.");
        }
        foreach (var (key, expected) in new[] { ("z", baseline.Data), ("y", changed.Data), ("z", baseline.Data) })
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
            Assert.AreEqual(expected, observed.Data, "Group and all newly created members must undo/redo together.");
        }
        // A second creation after undo proves that the existing member's parent
        // pointer was restored, not merely omitted from serialized observations.
        var restored = await Read(); batch.ExpectedRevision = restored.Revision;
        batch.OperationId = Guid.NewGuid().ToString("D");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(changed.Data, (await Read()).Data);
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        cleanup.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var observed = await Read(cleanup.Token);
            if (observed.Data.Equals(baseline.Data)) break;
            await Task.Delay(100, cleanup.Token);
        }
        var beforePersist = await Read(); batch.ExpectedRevision = beforePersist.Revision;
        batch.OperationId = Guid.NewGuid().ToString("D");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopened = await Read();
        Assert.IsTrue(changed.Data.Equals(reopened.Data),
            "Saving and reopening must preserve the complete screen, including symbol graphic identities.\n"
            + NativeSnapshotDifference.Describe(changed.Data, reopened.Data));
        foreach (var symbol in reopened.Data.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                     .Select(i => i.Unpack<SchematicSymbolInstance>()))
        {
            var graphics = symbol.Definition.Items.Select(i => i.Item)
                .Where(i => i.Is(SchematicGraphicShape.Descriptor)).Select(i => i.Unpack<SchematicGraphicShape>()).ToArray();
            Assert.AreEqual(8, graphics.Length, "The persistence fixture must cover seven shape primitives plus an identical duplicate circle.");
            Assert.AreEqual(8, graphics.Select(i => i.Id.Value).Distinct().Count(), "Identical graphics must retain separate identities.");
        }
        Assert.IsTrue(reopened.Data.Items.Contains(Any.Pack(inner)));
        Assert.IsTrue(reopened.Data.Items.Contains(Any.Pack(outer)));
        Assert.IsTrue(reopened.Data.Items.Contains(Any.Pack(note)));
        var savedReparent = reopened.Data.Clone();
        savedReparent.Items[savedReparent.Items.IndexOf(Any.Pack(inner))] = Any.Pack(revisedInner);
        savedReparent.Items[savedReparent.Items.IndexOf(Any.Pack(outer))] = Any.Pack(revisedOuter);
        var persistentRegroup = new ApplySchematicItemBatch { Document = document, ExpectedRevision = reopened.Revision,
            DocumentEpoch = reopened.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        persistentRegroup.Operations.Add(SchematicItemDelta.Plan(reopened.Data, savedReparent));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(persistentRegroup, token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var reopenedRegroup = await Read();
        Assert.IsTrue(savedReparent.Equals(reopenedRegroup.Data), "Reparented memberships and every object property must survive native reload.");
        var restoreGroups = new ApplySchematicItemBatch { Document = document, ExpectedRevision = reopenedRegroup.Revision,
            DocumentEpoch = reopenedRegroup.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        restoreGroups.Operations.Add(SchematicItemDelta.Plan(reopenedRegroup.Data, reopened.Data));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(restoreGroups, token);
        await VerifyDeletedGroupTransfer(client, document, inner, outer, processId, display, token, persist: true);
        reopened = await Read();
        var finalData = reopened.Data.Clone();
        finalData.Items.Remove(Any.Pack(inner)); finalData.Items.Remove(Any.Pack(outer)); finalData.Items.Remove(Any.Pack(note));
        var finalRemoval = new ApplySchematicItemBatch { Document = document, ExpectedRevision = reopened.Revision,
            DocumentEpoch = reopened.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        finalRemoval.Operations.Add(SchematicItemDelta.Plan(reopened.Data, finalData));
        Assert.AreEqual(3, finalRemoval.Operations.Count);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(finalRemoval, token);
        Assert.AreEqual(finalData, (await Read()).Data);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        var persisted = (await Read()).Data;
        Assert.AreEqual(finalData.Metadata, persisted.Metadata);
        Assert.AreEqual(finalData.Items.Count, persisted.Items.Count);
        Assert.IsTrue(finalData.Equals(persisted), "Group removal and all screen content must persist exactly.");
    }
}
