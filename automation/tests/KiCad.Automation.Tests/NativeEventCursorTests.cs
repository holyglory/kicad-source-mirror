using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeEventCursorTests
{
    private static AutomationSession Session() => new()
    {
        ProtocolVersion = 1, InstanceId = "fixture-instance", Epoch = "fixture-process",
        EventEpoch = "fixture-stream", EventEndpoint = "ipc:///tmp/event-fixture.sock"
    };

    private static AutomationEvent Packet(ulong sequence, bool change = false)
    {
        var packet = new AutomationEvent
        {
            ProtocolVersion = 1, InstanceId = "fixture-instance", ProcessEpoch = "fixture-process",
            EventEpoch = "fixture-stream", Sequence = sequence, Heartbeat = new()
        };
        if (change)
        {
            packet.SchematicCommit = new()
            {
                Document = new() { Type = (Kiapi.Common.Types.DocumentType)1,
                    Project = new() { Name = "fixture", Path = "/tmp/" }, SheetPath = new() },
                Revision = new() { Epoch = "fixture-document", Sequence = sequence },
                Change = new() { Kind = (SchematicChange.Types.Kind)1, Sequence = sequence }
            };
            packet.SchematicCommit.Document.SheetPath.Path.Add(new Kiapi.Common.Types.KIID
                { Value = "53613625-ae60-4c52-a803-99da3bbf53ab" });
        }
        return packet;
    }

    [TestMethod]
    public void InitialAttachmentAndMissingFinalChangeRequireStateRecovery()
    {
        var cursor = new NativeEventCursor(Session());
        Assert.AreEqual(NativeEventDisposition.InitialStateRequired, cursor.Observe(Packet(4)).Disposition);
        Assert.AreEqual(NativeEventDisposition.Heartbeat, cursor.Observe(Packet(4)).Disposition);
        Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(Packet(5, true)).Disposition);
        Assert.AreEqual(NativeEventDisposition.RecoveryRequired, cursor.Observe(Packet(6)).Disposition);
        Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(Packet(7, true)).Disposition);
    }

    [TestMethod]
    public void GapsDuplicatesAndInvalidFutureResumeAreNotAppliedAsNormalChanges()
    {
        var cursor = new NativeEventCursor(Session(), 4);
        Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(Packet(5, true)).Disposition);
        Assert.AreEqual(NativeEventDisposition.Duplicate, cursor.Observe(Packet(5, true)).Disposition);
        Assert.AreEqual(NativeEventDisposition.Duplicate, cursor.Observe(Packet(4)).Disposition);
        Assert.AreEqual(NativeEventDisposition.RecoveryRequired, cursor.Observe(Packet(9, true)).Disposition);
        Assert.AreEqual(NativeEventDisposition.RecoveryRequired,
            new NativeEventCursor(Session(), 10).Observe(Packet(5)).Disposition);
        Assert.AreEqual(NativeEventDisposition.Heartbeat,
            new NativeEventCursor(Session(), ulong.MaxValue).Observe(Packet(ulong.MaxValue)).Disposition);
    }

    [TestMethod]
    public void WrongEpochsInvalidPayloadAndMalformedIdentityDoNotAdvanceTheCursor()
    {
        foreach (Action<AutomationEvent> corrupt in new Action<AutomationEvent>[]
        {
            packet => packet.ProtocolVersion = 2,
            packet => packet.InstanceId = "other-instance",
            packet => packet.ProcessEpoch = "other-process",
            packet => packet.EventEpoch = "other-stream",
            packet => packet.ClearPayload(),
            packet => packet.SchematicCommit.Document.SheetPath.Path.Clear(),
            packet => packet.SchematicCommit.Document.SheetPath.Path[0].Value = Guid.Empty.ToString("D"),
            packet => packet.SchematicCommit.Revision.Sequence = 2,
            packet => packet.SchematicCommit.Change.Kind = (SchematicChange.Types.Kind)0
        })
        {
            var cursor = new NativeEventCursor(Session(), 0);
            var invalid = Packet(1, true); corrupt(invalid);
            Assert.ThrowsExactly<AutomationException>(() => cursor.Observe(invalid));
            Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(Packet(1, true)).Disposition);
        }
    }

    [TestMethod]
    public void DiscoveryAndReturnedMessagesDoNotShareMutableStateWithTheCursor()
    {
        var session = Session(); var cursor = new NativeEventCursor(session, 0);
        session.Epoch = "mutated";
        var packet = Packet(1, true); var delivery = cursor.Observe(packet);
        packet.Sequence = 99;
        Assert.AreEqual(1UL, delivery.Event.Sequence);
        Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(Packet(2, true)).Disposition);
    }

    [TestMethod]
    public void StreamAndDocumentJournalCountersRemainIndependent()
    {
        var cursor = new NativeEventCursor(Session(), 162);
        var packet = Packet(163, true);
        packet.SchematicCommit.Revision.Sequence = 23;
        packet.SchematicCommit.Change.Sequence = 23;
        Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(packet).Disposition);
        var next = Packet(164, true);
        next.SchematicCommit.Revision.Sequence = 24;
        next.SchematicCommit.Change.Sequence = 23;
        Assert.ThrowsExactly<AutomationException>(() => cursor.Observe(next));
        next.SchematicCommit.Change.Sequence = 24;
        Assert.AreEqual(NativeEventDisposition.Change, cursor.Observe(next).Disposition);
    }
}
