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
    private static async Task VerifyNativeAssetRestore(NativeClient client, DocumentSpecifier document,
        string textId, int processId, string display, CancellationToken token)
    {
        async Task<SchematicMetadataSnapshot> Read(CancellationToken? local = null) =>
            await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document }, local ?? token);
        var initial = await Read();
        var restored = (SchematicMetadata)SchematicDataXml.Read(SchematicDataXml.Write(initial.Metadata));
        Assert.AreEqual(1, restored.EmbeddedFiles.Files.Count);
        SchematicItemOperation Assets(EmbeddedFiles files) => new()
        {
            ReplaceEmbeddedFiles = new() { Files = files, EmbeddedFonts = restored.EmbeddedFonts }
        };
        ApplySchematicItemBatch Batch(params SchematicItemOperation[] operations)
        {
            var request = new ApplySchematicItemBatch { Document = document, Description = "Restore XML embedded assets" };
            request.Operations.AddRange(operations);
            return request;
        }
        async Task<SchematicItemBatchResult> Apply(ApplySchematicItemBatch request) =>
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token);
        async Task<ApplySchematicItemBatch> PlanAssets(EmbeddedFiles files, Action<SchematicScreenData>? editItems = null)
        {
            var observed = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, token);
            var desired = observed.Data.Clone();
            desired.Metadata.EmbeddedFiles = files.Clone();
            editItems?.Invoke(desired);
            desired = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(desired));
            var request = new ApplySchematicItemBatch
            {
                Document = document, Description = "Apply XML-planned assets and items",
                ExpectedRevision = observed.Revision, DocumentEpoch = observed.Revision.Epoch,
                OperationId = Guid.NewGuid().ToString("D")
            };
            request.Operations.Add(SchematicItemDelta.Plan(observed.Data, desired));
            return request;
        }
        var textQuery = new GetItemsById { Header = new() { Document = document } };
        textQuery.Items.Add(new KIID { Value = textId });
        var originalText = await client.InvokeAsync<GetItemsById, GetItemsResponse>(textQuery, token);
        async Task VerifyTextCount(int count)
        {
            if (count == 0)
                Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<GetItemsById, GetItemsResponse>(textQuery, token))).Status);
            else
                Assert.AreEqual(originalText, await client.InvokeAsync<GetItemsById, GetItemsResponse>(textQuery, token));
        }

        // A mixed operation has one native undo step for both graphical and
        // schematic-owned data, not two unrelated undo entries.
        var remove = await PlanAssets(new(), data =>
            data.Items.Remove(data.Items.Single(item => item.Is(SchematicText.Descriptor)
                && item.Unpack<SchematicText>().Id.Value == textId)));
        Assert.AreEqual(2, remove.Operations.Count);
        remove.OperationId = Guid.NewGuid().ToString("D");
        remove.DocumentEpoch = initial.Revision.Epoch;
        remove.ExpectedRevision = initial.Revision.Clone();
        Assert.IsTrue((await Apply(remove)).EmbeddedFilesChanged);
        var empty = await Read();
        Assert.AreEqual(0, empty.Metadata.EmbeddedFiles.Files.Count);
        await VerifyTextCount(0);
        Assert.IsTrue((await Apply(remove)).EmbeddedFilesChanged); // Replays receipt, not the mutation.
        Assert.AreEqual(empty, await Read());

        foreach (var (key, expected, count, kind) in new[]
        {
            ("z", restored.EmbeddedFiles, 1, SchematicChange.Types.Kind.Undo),
            ("y", new EmbeddedFiles(), 0, SchematicChange.Types.Kind.Redo)
        })
        {
            var before = await Read();
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicMetadataSnapshot changed;
            do
            {
                changed = await Read(deadline.Token);
                if (changed.Revision.Equals(before.Revision)) await Task.Delay(100, deadline.Token);
            } while (changed.Revision.Equals(before.Revision));
            Assert.AreEqual(expected, changed.Metadata.EmbeddedFiles);
            await VerifyTextCount(count);
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = document, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
            Assert.AreEqual(kind, journal.Changes.Single().Kind);
        }

        // Recreate the removed text and restore the native asset from typed XML.
        var recover = await PlanAssets(restored.EmbeddedFiles,
            data => data.Items.Add(originalText.Items.Single().Clone()));
        Assert.AreEqual(2, recover.Operations.Count);
        Assert.IsTrue((await Apply(recover)).EmbeddedFilesChanged);
        Assert.AreEqual(restored, (await Read()).Metadata);
        var recovered = await Read();
        Assert.AreEqual(0, (await PlanAssets(restored.EmbeddedFiles)).Operations.Count);
        Assert.IsFalse((await Apply(Batch(Assets(restored.EmbeddedFiles)))).EmbeddedFilesChanged);
        Assert.AreEqual(recovered, await Read(), "Unchanged asset synchronization must not create a revision or undo entry.");
        var legacy = restored.EmbeddedFiles.Clone();
        legacy.Files[0].DataHash = "9AB164C455965EC6CA9F3928F341F0B3";
        Assert.IsFalse((await Apply(Batch(Assets(legacy)))).EmbeddedFilesChanged);
        Assert.AreEqual(recovered, await Read(), "Native checksum migration must not create a spurious asset edit.");

        var invalid = restored.EmbeddedFiles.Clone();
        invalid.Files[0].DataHash = "00000000000000000000000000000000";
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(
            new() { Remove = new KIID { Value = textId } }, Assets(invalid))));
        Assert.AreEqual(recovered, await Read());
        Assert.AreEqual(originalText, await client.InvokeAsync<GetItemsById, GetItemsResponse>(textQuery, token));
        // Also roll back already-applied assets when a later operation fails.
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(Assets(new()),
            new() { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } })));
        Assert.AreEqual(recovered, await Read());
        invalid = restored.EmbeddedFiles.Clone();
        invalid.Files.Add(invalid.Files[0].Clone());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(Assets(invalid))));
        Assert.AreEqual(recovered, await Read());
        foreach (Action<EmbeddedFile> corrupt in new Action<EmbeddedFile>[]
        {
            f => f.Name = "",
            f => f.Name = "../wrong.png",
            f => f.Type = (EmbeddedFileType)987,
            f => f.Data = Google.Protobuf.ByteString.CopyFromUtf8("invalid compressed payload")
        })
        {
            invalid = restored.EmbeddedFiles.Clone(); corrupt(invalid.Files[0]);
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(Batch(Assets(invalid))));
            Assert.AreEqual(recovered, await Read());
        }
        var stale = Batch(Assets(new())); stale.ExpectedRevision = initial.Revision;
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => Apply(stale));
        Assert.AreEqual(recovered, await Read());
        // An assets-only commit must not be discarded as an empty item list.
        Assert.IsTrue((await Apply(Batch(Assets(new())))).EmbeddedFilesChanged);
        var assetsOnly = await Read();
        Assert.AreEqual(0, assetsOnly.Metadata.EmbeddedFiles.Files.Count);
        Assert.AreEqual(originalText, await client.InvokeAsync<GetItemsById, GetItemsResponse>(textQuery, token));
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicMetadataSnapshot changed;
            do
            {
                changed = await Read(deadline.Token);
                if (changed.Revision.Equals(assetsOnly.Revision)) await Task.Delay(100, deadline.Token);
            } while (changed.Revision.Equals(assetsOnly.Revision));
            Assert.AreEqual(restored, changed.Metadata);
        }
        // Save a distinguishable asset set, then use native reload rather than
        // mistaking an in-memory metadata read for persistence evidence.
        var savedFiles = restored.EmbeddedFiles.Clone();
        var extra = savedFiles.Files[0].Clone(); extra.Name = "xml-restored-copy.png";
        savedFiles.Files.Add(extra);
        var persist = await PlanAssets(savedFiles);
        Assert.AreEqual(1, persist.Operations.Count);
        Assert.IsTrue((await Apply(persist)).EmbeddedFilesChanged);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = document }, token);
        Assert.AreEqual(savedFiles, (await Read()).Metadata.EmbeddedFiles);
        Assert.IsTrue((await Apply(Batch(Assets(restored.EmbeddedFiles)))).EmbeddedFilesChanged);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        Assert.AreEqual(restored, (await Read()).Metadata);
    }
}
