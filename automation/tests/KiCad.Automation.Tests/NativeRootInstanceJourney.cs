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
    private static async Task VerifyRootInstanceXml(NativeClient client, DocumentSpecifier document,
        int processId, string display, CancellationToken token)
    {
        async Task<SchematicMetadataSnapshot> Read(CancellationToken? local = null) =>
            await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document }, local ?? token);
        var original = await Read();
        Assert.IsNotNull(original.Metadata.RootInstance);
        Assert.IsTrue(original.Metadata.RootInstance.HasPageNumber);
        var xml = SchematicDataXml.Write(original.Metadata);
        var restored = (SchematicMetadata)SchematicDataXml.Read(xml);
        Assert.AreEqual(original.Metadata, restored);
        Assert.IsFalse(restored.UnrepresentedState.Contains("root_sheet_records"));
        ApplySchematicItemBatch Batch(SchematicRootInstance state)
        {
            var batch = new ApplySchematicItemBatch { Document = document, Description = "XML root page record" };
            batch.Operations.Add(new SchematicItemOperation { SetRootInstance = state });
            return batch;
        }
        async Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch batch) =>
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var desired = new SchematicRootInstance { PageNumber = "10.A" };
        Assert.AreNotEqual(desired, restored.RootInstance);
        var change = Batch(desired);
        var beforeData = new SchematicScreenData { Metadata = restored.Clone() };
        var desiredData = beforeData.Clone(); desiredData.Metadata.RootInstance = desired.Clone();
        desiredData = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desiredData));
        change.Operations.Clear();
        change.Operations.Add(SchematicItemDelta.Plan(beforeData, desiredData));
        Assert.AreEqual(1, change.Operations.Count);
        change.OperationId = Guid.NewGuid().ToString("D");
        change.DocumentEpoch = original.Revision.Epoch;
        change.ExpectedRevision = original.Revision.Clone();
        Assert.IsTrue((await Apply(change)).RootInstanceChanged);
        var changed = await Read();
        Assert.AreEqual(desired, changed.Metadata.RootInstance);
        Assert.IsTrue((await Apply(change)).RootInstanceChanged);
        Assert.AreEqual(changed, await Read());
        Assert.IsFalse((await Apply(Batch(desired))).RootInstanceChanged);
        Assert.AreEqual(changed, await Read());

        foreach (var (key, expected) in new[] { ("z", restored.RootInstance), ("y", desired) })
        {
            var before = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicMetadataSnapshot observed;
            do
            {
                observed = await Read(deadline.Token);
                if (observed.Revision.Equals(before.Revision)) await Task.Delay(100, deadline.Token);
            } while (observed.Revision.Equals(before.Revision));
            Assert.AreEqual(expected, observed.Metadata.RootInstance);
        }
        var stable = await Read();
        foreach (string invalid in new[] { "", "10 A", "10\nA", "10\tA" })
        {
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(new() { PageNumber = invalid })));
            Assert.AreEqual(stable, await Read());
        }
        var failed = Batch(restored.RootInstance);
        failed.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(failed));
        Assert.AreEqual(stable, await Read());
        var stale = Batch(restored.RootInstance); stale.ExpectedRevision = original.Revision;
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.AreEqual(stable, await Read());

        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        Assert.AreEqual(desired, (await Read()).Metadata.RootInstance);
        Assert.IsTrue((await Apply(Batch(restored.RootInstance))).RootInstanceChanged);
        Assert.AreEqual(restored, (await Read()).Metadata);
        Assert.IsTrue((await Apply(Batch(new()))).RootInstanceChanged);
        var absent = await Read();
        Assert.IsFalse(absent.Metadata.RootInstance.HasPageNumber);
        Assert.AreEqual(absent.Metadata, SchematicDataXml.Read(SchematicDataXml.Write(absent.Metadata)));
        Assert.IsTrue((await Apply(Batch(restored.RootInstance))).RootInstanceChanged);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
    }
}
