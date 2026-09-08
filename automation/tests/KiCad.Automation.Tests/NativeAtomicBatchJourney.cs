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
    private static async Task VerifyAtomicBatch(NativeClient client, DocumentSpecifier document,
        string textId, int processId, string display, CancellationToken token)
    {
        var query = new GetItemsById { Header = new ItemHeader { Document = document } };
        query.Items.Add(new KIID { Value = textId });
        async Task<SchematicText> ReadText() =>
            (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicText>();
        var original = await ReadText();
        var moved = original.Clone();
        moved.Text.Position.XNm += 5000000;
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var cursor = new ReadSchematicChangeJournal
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
        };
        var missing = new SchematicItemOperation { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } };
        foreach (var operations in new[]
        {
            new[] { new SchematicItemOperation { Update = Any.Pack(moved) } },
            new[] { new SchematicItemOperation { Remove = new KIID { Value = textId } } },
            new[] { new SchematicItemOperation { Update = Any.Pack(moved) },
                    new SchematicItemOperation { Remove = new KIID { Value = textId } } }
        })
        {
            var rejected = new ApplySchematicItemBatch { Document = document };
            rejected.Operations.Add(operations);
            rejected.Operations.Add(missing);
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token))).Status);
            Assert.AreEqual(original, await ReadText(), "Rejected batches must restore earlier operations.");
            Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);
        }

        foreach (var afterRemoval in new[]
        {
            new SchematicItemOperation { Update = Any.Pack(moved) },
            new SchematicItemOperation { Remove = new KIID { Value = textId } }
        })
        {
            var invalidOrder = new ApplySchematicItemBatch { Document = document };
            invalidOrder.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = textId } });
            invalidOrder.Operations.Add(afterRemoval);
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalidOrder, token))).Status);
            Assert.AreEqual(original, await ReadText());
            Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);
        }

        // Two updates to one object must remain one undoable native operation.
        var final = moved.Clone();
        final.Text.Position.YNm += 5000000;
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Atomic layout fixture" };
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(moved) });
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(final) });
        var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(final, await ReadText());
        var committed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(1, committed.Changes.Count);
        cursor.AfterSequence = committed.Sequence;
        foreach (var (key, expected) in new[] { ("z", original), ("y", final) })
        {
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            inputDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicChangeJournal changed;
            do
            {
                changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, inputDeadline.Token);
                if (changed.Changes.Count == 0) await Task.Delay(100, inputDeadline.Token);
            } while (changed.Changes.Count == 0);
            Assert.AreEqual(1, changed.Changes.Count);
            Assert.AreEqual(expected, await ReadText());
            cursor.AfterSequence = changed.Sequence;
        }

        var deletion = new ApplySchematicItemBatch { Document = document, Description = "Atomic modify then delete" };
        deletion.Operations.Add(new SchematicItemOperation { Update = Any.Pack(original) });
        deletion.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = textId } });
        Assert.AreEqual(1, (await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(deletion, token)).Removed.Count);
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))).Status);
        var deleted = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(1, deleted.Changes.Count);
        cursor.AfterSequence = deleted.Sequence;
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        using var undoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        undoDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        SchematicChangeJournal undone;
        do
        {
            undone = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, undoDeadline.Token);
            if (undone.Changes.Count == 0) await Task.Delay(100, undoDeadline.Token);
        } while (undone.Changes.Count == 0);
        Assert.AreEqual(final, await ReadText(), "Undo deletion must restore the pre-batch value, not its intermediate edit.");
        await VerifyAtomicCreation(client, document, original, processId, display, token);
    }
}
