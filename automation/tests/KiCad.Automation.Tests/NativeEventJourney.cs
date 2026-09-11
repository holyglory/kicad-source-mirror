using Google.Protobuf;
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
    private static async Task VerifyNativeEvents(NativeClient client, NativeClient otherClient,
        DocumentSpecifier document, string textId, int processId, string display, string evidence,
        string instanceId, CancellationToken token)
    {
        var session = await client.HandshakeAsync(token);
        var other = await otherClient.HandshakeAsync(token);
        Assert.AreNotEqual(session.EventEndpoint, other.EventEndpoint);
        Assert.AreNotEqual(session.EventEpoch, other.EventEpoch);
        using var first = new NativeEventSubscription(session);
        using var second = new NativeEventSubscription(session);
        using var isolated = new NativeEventSubscription(other);
        var initial = await Task.WhenAll(Receive(first), Receive(second), Receive(isolated));
        Assert.IsTrue(initial.All(value => value.Disposition == NativeEventDisposition.InitialStateRequired));
        await using var intake = await NativeRecoveryIntakeProbe.StartAsync(client, document, evidence, instanceId, token);
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var query = new GetItemsById { Header = new() { Document = document } };
        query.Items.Add(new KIID { Value = textId });
        async Task<SchematicText> Text() =>
            (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SchematicText>();
        var original = await Text(); var moved = original.Clone(); moved.Text.Position.XNm += 2540000;
        var batch = new ApplySchematicItemBatch
        {
            Document = document, DocumentEpoch = journal.DocumentEpoch,
            ExpectedRevision = new() { Epoch = journal.DocumentEpoch, Sequence = journal.Sequence },
            OperationId = Guid.NewGuid().ToString("D"), OriginId = Guid.NewGuid().ToString("D"),
            Description = "Native event delivery fixture"
        };
        batch.Operations.Add(new SchematicItemOperation { Update = Any.Pack(moved) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var committed = await Change(1);
        Assert.AreEqual(batch.OperationId, committed.SchematicCommit.Change.OperationId);
        Assert.AreEqual(batch.OriginId, committed.SchematicCommit.Change.OriginId);
        Assert.AreEqual(moved, await Text());
        // An operation-ID retry returns its original result without publishing
        // another edit; a rejected stale operation also leaves the event head.
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(rejected.Message, "Stale document revision");
        Assert.AreEqual(journal.Sequence, (await client.InvokeAsync<ReadSchematicChangeJournal,
            SchematicChangeJournal>(new() { Document = document }, token)).Sequence);
        var unchangedHead = await Receive(first);
        Assert.AreEqual(NativeEventDisposition.Heartbeat, unchangedHead.Disposition);
        Assert.AreEqual(committed.Sequence, unchangedHead.Event.Sequence);
        Assert.AreEqual(moved, await Text());
        var otherHeartbeat = await Receive(isolated);
        Assert.AreEqual(NativeEventDisposition.Heartbeat, otherHeartbeat.Disposition);
        Assert.AreEqual(initial[2].Event.Sequence, otherHeartbeat.Event.Sequence);

        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await Change(2);
        Assert.AreEqual(original, await Text());
        NativeKeyboard.SchematicShortcut(display, processId, "y");
        await Change(3);
        Assert.AreEqual(moved, await Text());
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await Change(2);
        Assert.AreEqual(original, await Text());

        await intake.VerifyStopAndMcpDisconnect();

        // Cancellation releases only this subscriber. The same process, dirty
        // state and command channel remain available, and another observer works.
        var saveState = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);
        using (var cancelled = new NativeEventSubscription(session))
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await Receive(cancelled);
            var waiting = cancelled.ReceiveAsync(cancel.Token);
            cancel.Cancel();
            try { await waiting; Assert.Fail("The cancelled observer returned a successful event."); }
            catch (OperationCanceledException) { Assert.IsTrue(waiting.IsCanceled); }
        }
        Assert.AreEqual(session.Epoch, (await client.HandshakeAsync(token)).Epoch);
        Assert.AreEqual(saveState, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token));
        using var recovered = new NativeEventSubscription(await client.HandshakeAsync(token));
        Assert.AreEqual(NativeEventDisposition.InitialStateRequired, (await Receive(recovered)).Disposition);

        // A deliberately stale resumed cursor must request recovery even if the
        // missing last change is followed only by cursor heartbeats.
        using var missed = new NativeEventSubscription(session, initial[0].Event.Sequence);
        Assert.AreEqual(NativeEventDisposition.RecoveryRequired, (await Receive(missed)).Disposition);

        async Task<NativeEventDelivery> Receive(NativeEventSubscription subscription)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return await subscription.ReceiveAsync(timeout.Token);
        }

        async Task<AutomationEvent> Change(int kind)
        {
            async Task<NativeEventDelivery> Next(NativeEventSubscription subscription)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var delivery = await subscription.ReceiveAsync(timeout.Token);
                    if (delivery.Disposition == NativeEventDisposition.Heartbeat) continue;
                    return delivery;
                }
            }
            var deliveries = await Task.WhenAll(Next(first), Next(second));
            Assert.IsTrue(deliveries.All(value => value.Disposition == NativeEventDisposition.Change));
            Assert.AreEqual(deliveries[0].Event, deliveries[1].Event);
            var notification = deliveries[0].Event;
            Assert.AreEqual(document, notification.SchematicCommit.Document);
            Assert.AreEqual(journal.DocumentEpoch, notification.SchematicCommit.Revision.Epoch);
            Assert.AreEqual(++journal.Sequence, notification.SchematicCommit.Revision.Sequence);
            Assert.AreEqual(kind, (int)notification.SchematicCommit.Change.Kind);
            if (kind != 1)
            {
                Assert.IsEmpty(notification.SchematicCommit.Change.OperationId);
                Assert.IsEmpty(notification.SchematicCommit.Change.OriginId);
            }
            Assert.IsFalse(notification.SchematicCommit.TrackingComplete);
            await File.WriteAllTextAsync(Path.Combine(evidence,
                instanceId + "-native-event-" + notification.Sequence + ".json"), JsonFormatter.Default.Format(notification), token);
            await intake.WaitForRevision(notification.SchematicCommit.Revision.Sequence);
            return notification;
        }
    }
}
