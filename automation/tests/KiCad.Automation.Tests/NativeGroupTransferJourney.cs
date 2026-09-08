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
    private static async Task VerifyDeletedGroupTransfer(NativeClient client, DocumentSpecifier document,
        Group inner, Group outer, int processId, string display, CancellationToken token, bool persist = false)
    {
        Task<SchematicScreenDataSnapshot> Read(CancellationToken? local = null) =>
            client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, local ?? token);

        foreach (bool newParent in new[] { false, true })
        {
            var baseline = await Read(); var desired = baseline.Data.Clone();
            desired.Items.Remove(Any.Pack(inner)); desired.Items.Remove(Any.Pack(outer));
            var parent = outer.Clone(); parent.Items.Clear(); parent.Items.Add(inner.Items.Select(i => i.Clone()));
            if (newParent) parent.Id.Value = Guid.NewGuid().ToString("D");
            desired.Items.Add(Any.Pack(parent));
            var batch = new ApplySchematicItemBatch { Document = document, ExpectedRevision = baseline.Revision,
                DocumentEpoch = baseline.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            batch.Operations.Add(SchematicItemDelta.Plan(baseline.Data, desired));
            var rejected = batch.Clone(); rejected.OperationId = Guid.NewGuid().ToString("D");
            rejected.Operations.Add(new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
            Assert.AreEqual(baseline, await Read(), "Failed transfer out of a deleted group must restore state and revision.");
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            var changed = await Read();
            Assert.AreEqual(0, SchematicItemDelta.Plan(changed.Data, desired).Count,
                "Surviving members must move to the intended group without recreation or content changes.");
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
            Assert.AreEqual(changed, await Read(), "Retry must not repeat deletion or transfer.");
            if (persist)
            {
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
                var saved = await Read();
                Assert.IsTrue(changed.Data.Equals(saved.Data), "Transferred survivors and removed groups must persist exactly.");
                var restore = new ApplySchematicItemBatch { Document = document, ExpectedRevision = saved.Revision,
                    DocumentEpoch = saved.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
                restore.Operations.Add(SchematicItemDelta.Plan(saved.Data, baseline.Data));
                await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(restore, token);
                Assert.AreEqual(0, SchematicItemDelta.Plan((await Read()).Data, baseline.Data).Count,
                    "Reverse synchronization must recreate the deleted group with its original identity and membership.");
                await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
                await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
                Assert.AreEqual(0, SchematicItemDelta.Plan((await Read()).Data, baseline.Data).Count);
                continue;
            }
            foreach (var (key, expected) in new[] { ("z", baseline.Data), ("y", changed.Data), ("z", baseline.Data) })
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
                Assert.IsTrue(expected.Equals(observed.Data), "Undo/redo must restore removed groups and their original members.");
            }
        }
    }
}
