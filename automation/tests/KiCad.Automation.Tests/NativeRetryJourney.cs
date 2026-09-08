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
    private static async Task<ApplySchematicItemBatch> VerifyNativeRetry(NativeClient client, DocumentSpecifier document,
        string textId, string evidence, CancellationToken token)
    {
        var query = new GetItemsById { Header = new ItemHeader { Document = document } };
        query.Items.Add(new KIID { Value = textId });
        var original = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token))
            .Items.Single().Unpack<SchematicText>();
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var beforeHierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
            new() { Document = document }, token);
        Assert.AreEqual(journal.Sequence, beforeHierarchy.Revision.Sequence);
        var session = await client.HandshakeAsync(token);
        var originId = Guid.NewGuid();
        var cursor = new ReadSchematicChangeJournal
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch, AfterSequence = journal.Sequence
        };
        var changed = original.Clone(); changed.Text.Position.XNm += 1000000;
        var request = new ApplySchematicItemBatch
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch,
            ExpectedRevision = new() { Epoch = journal.DocumentEpoch, Sequence = journal.Sequence },
            OperationId = Guid.NewGuid().ToString("D"), OriginId = originId.ToString("D"),
            Description = "Native retry identity fixture"
        };
        request.Operations.Add(new SchematicItemOperation { Update = Any.Pack(changed) });
        // This recovery fixture deliberately leaves engineering bindings unresolved. It
        // verifies preservation of a real native hierarchy, not circuit reconstruction.
        var sheetDefinition = Guid.NewGuid();
        var engineering = new KiCad.Automation.Model.EngineeringDesign(
            new(Guid.NewGuid(), [], [new(sheetDefinition, "Recovery fixture", [])],
                [new(Guid.NewGuid(), sheetDefinition, null)], [], [], []),
            new(Guid.NewGuid(), [], [], [], []), [], []);
        var baseline = new SchematicDesign(engineering, beforeHierarchy.Data, [], []);
        string recoveryPath = Path.Combine(evidence, $"{request.OperationId}-design-recovery.json");
        var recovery = new DesignRecoveryStore(recoveryPath);
        var savedRecovery = recovery.Save(new(originId, Guid.Parse(session.InstanceId),
            new(journal.DocumentEpoch, journal.Sequence), beforeHierarchy.TrackingComplete, baseline,
            [0xff, 0x3c], beforeHierarchy.Data, [], request), null);
        var inspection = new InspectSchematicOperation
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch, OperationId = request.OperationId
        };
        Assert.AreEqual(SchematicOperationReceipt.Types.State.NotFound,
            (await client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(inspection, token)).State);
        var applied = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token);
        Assert.AreEqual(journal.DocumentEpoch, applied.Revision.Epoch);
        Assert.AreEqual(journal.Sequence + 1, applied.Revision.Sequence);
        Assert.IsFalse(applied.TrackingComplete);
        var receipt = await client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(inspection, token);
        Assert.AreEqual(SchematicOperationReceipt.Types.State.Completed, receipt.State);
        Assert.AreEqual(applied, receipt.Result);
        Assert.AreEqual(document, receipt.Document);
        Assert.IsFalse(receipt.ExpectedRequestVerified, "ID-only lookup must not imply payload verification.");
        inspection.ExpectedRequest = request.Clone();
        var verified = await client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(inspection, token);
        Assert.IsTrue(verified.ExpectedRequestVerified);
        Assert.AreEqual(applied, verified.Result);
        var wrongPayload = inspection.Clone();
        wrongPayload.ExpectedRequest.Description = "Another request using the retained identity";
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(wrongPayload, token));
        var wrongIdentity = inspection.Clone();
        wrongIdentity.ExpectedRequest.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(wrongIdentity, token));
        var after = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(cursor.AfterSequence + 1, after.Sequence);
        cursor.AfterSequence = after.Sequence;
        Assert.AreEqual(applied, await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token));
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);
        var reconnected = new NativeClient(new NngTransport(), client.Endpoint, client.Epoch);
        await reconnected.HandshakeAsync(token);
        Assert.IsTrue((await reconnected.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(inspection, token))
            .ExpectedRequestVerified, "A new connection must inspect the same retained command without resubmitting it.");
        Assert.AreEqual(applied, await reconnected.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token));

        var reused = request.Clone(); reused.Description = "Different payload with same ID";
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(reused, token));
        var wrongEpoch = request.Clone(); wrongEpoch.DocumentEpoch = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(wrongEpoch, token));
        var missingEpoch = request.Clone(); missingEpoch.DocumentEpoch = "";
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(missingEpoch, token));
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);

        foreach (var revision in new[]
        {
            request.ExpectedRevision.Clone(),
            new DocumentRevision { Epoch = journal.DocumentEpoch, Sequence = after.Sequence + 1 },
            new DocumentRevision { Epoch = Guid.NewGuid().ToString("D"), Sequence = after.Sequence }
        })
        {
            var stale = request.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
            stale.ExpectedRevision = revision;
            stale.Operations.Clear(); stale.Operations.Add(new SchematicItemOperation { Update = Any.Pack(original) });
            var staleError = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
            StringAssert.Contains(staleError.Message, "Stale document revision");
            Assert.AreEqual(changed, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SchematicText>());
            var notAdmitted = inspection.Clone(); notAdmitted.OperationId = stale.OperationId;
            notAdmitted.ExpectedRequest = stale.Clone();
            Assert.AreEqual(SchematicOperationReceipt.Types.State.NotFound,
                (await client.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(notAdmitted, token)).State);
        }
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);

        var rejected = request.Clone(); rejected.OperationId = Guid.NewGuid().ToString("D");
        rejected.ExpectedRevision.Sequence = after.Sequence;
        rejected.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = Guid.NewGuid().ToString("D") } });
        var failure = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        var replayedFailure = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            reconnected.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        Assert.AreEqual(failure.Status, replayedFailure.Status);
        Assert.AreEqual(failure.Message, replayedFailure.Message);
        var rejectedInspection = inspection.Clone(); rejectedInspection.OperationId = rejected.OperationId;
        rejectedInspection.ExpectedRequest = rejected.Clone();
        var rejectedReceipt = await reconnected.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(rejectedInspection, token);
        Assert.AreEqual(SchematicOperationReceipt.Types.State.Rejected, rejectedReceipt.State);
        Assert.IsTrue(rejectedReceipt.ExpectedRequestVerified);
        Assert.AreEqual(failure.Status, rejectedReceipt.NativeStatusCode);
        Assert.AreEqual(failure.Message, rejectedReceipt.FailureMessage);
        var staleInspection = inspection.Clone(); staleInspection.DocumentEpoch = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            reconnected.InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(staleInspection, token));
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);

        // A later successful edit must not be overwritten by a retry of the
        // earlier request: replay its receipt, not its mutation.
        var restore = request.Clone(); restore.OperationId = Guid.NewGuid().ToString("D");
        restore.ExpectedRevision.Sequence = after.Sequence;
        restore.Operations.Clear(); restore.Operations.Add(new SchematicItemOperation { Update = Any.Pack(original) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(restore, token);
        cursor.AfterSequence = (await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Sequence;
        Assert.IsGreaterThan(applied.Revision.Sequence, cursor.AfterSequence,
            "Later edits advance the journal, not the original retained result.");
        Assert.AreEqual(applied, await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(request, token));
        Assert.AreEqual(original, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SchematicText>());
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);
        var recovered = await DesignRecoveryInspector.ObserveAsync(new DesignRecoveryStore(recoveryPath), reconnected, token);
        Assert.AreEqual(DesignRecoveryDisposition.CompletedNeedsReconciliation, recovered.Inspection.Disposition);
        Assert.AreEqual(applied, recovered.Inspection.Receipt!.Result);
        Assert.AreEqual(cursor.AfterSequence, recovered.Snapshot.Revision.Sequence);
        Assert.IsFalse(recovered.Snapshot.TrackingComplete);
        var recoveredText = recovered.Snapshot.Data.Instances.SelectMany(s => s.Items)
            .Where(o => o.Is(SchematicText.Descriptor)).Select(o => o.Unpack<SchematicText>())
            .Single(t => t.Id.Value == textId);
        Assert.AreEqual(original, recoveredText, "The current observation must retain the later restoring edit.");
        Assert.AreEqual(savedRecovery.RevisionToken, recovery.Read()!.RevisionToken);
        await File.WriteAllTextAsync(Path.Combine(evidence, $"{request.OperationId}-recovery-observed.xml"),
            SchematicDataXml.Write(recovered.Snapshot.Data), token);
        return request;
    }
}
