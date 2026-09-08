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
    private static async Task VerifyAtomicCreation(NativeClient client, DocumentSpecifier document,
        SchematicText template, int processId, string display, CancellationToken token)
    {
        var created = template.Clone();
        created.Id.Value = Guid.NewGuid().ToString("D");
        var moved = created.Clone();
        moved.Text.Position.YNm += 10000000;
        var query = new GetItemsById { Header = new ItemHeader { Document = document } };
        query.Items.Add(created.Id);
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var cursor = new ReadSchematicChangeJournal
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
        };
        var batch = new ApplySchematicItemBatch { Document = document, Description = "Create and arrange" };
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(created) });
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(moved) });
        var rejected = batch.Clone();
        rejected.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } });
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token))).Status);
        await AssertAbsent();
        Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);

        var locked = created.Clone();
        locked.Locked = (LockedState)2;
        foreach (var second in new[]
        {
            new SchematicItemOperation { Update = Any.Pack(moved) },
            new SchematicItemOperation { Remove = created.Id },
            new SchematicItemOperation { Create = Any.Pack(created) }
        })
        {
            var invalid = new ApplySchematicItemBatch { Document = document };
            invalid.Operations.Add(new SchematicItemOperation { Create = Any.Pack(locked) });
            invalid.Operations.Add(second);
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalid, token))).Status);
            await AssertAbsent();
            Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);
        }

        // Creating then deleting a new item has no persisted effect or undo entry.
        var cancelled = batch.Clone();
        cancelled.Operations.Add(new SchematicItemOperation { Remove = created.Id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(cancelled, token);
        await AssertAbsent();
        Assert.AreEqual(0, (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes.Count);

        var result = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(2, result.Items.Count);
        await AssertCreated();
        var committed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(1, committed.Changes.Count);
        cursor.AfterSequence = committed.Sequence;
        foreach (string key in new[] { "z", "y" })
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
            if (key == "z") await AssertAbsent(); else await AssertCreated();
            cursor.AfterSequence = changed.Sequence;
        }

        var replacement = new ApplySchematicItemBatch { Document = document };
        var replaced = moved.Clone();
        replaced.Text.Position.XNm += 5000000;
        replacement.Operations.Add(new SchematicItemOperation { Remove = created.Id });
        replacement.Operations.Add(new SchematicItemOperation { Create = Any.Pack(created) });
        replacement.Operations.Add(new SchematicItemOperation { Update = Any.Pack(replaced) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(replacement, token);
        Assert.AreEqual(replaced, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicText>());
        var replacedJournal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(1, replacedJournal.Changes.Count);
        cursor.AfterSequence = replacedJournal.Sequence;
        foreach (var (key, expected) in new[] { ("z", moved), ("y", replaced) })
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
            Assert.AreEqual(expected, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicText>());
            cursor.AfterSequence = changed.Sequence;
        }

        async Task AssertAbsent() => Assert.AreEqual(3,
            (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))).Status);

        async Task AssertCreated() => Assert.AreEqual(moved,
            (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
                .Items.Single().Unpack<SchematicText>());
    }
}
