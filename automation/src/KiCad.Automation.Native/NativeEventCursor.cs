using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public enum NativeEventDisposition { InitialStateRequired, Change, Heartbeat, RecoveryRequired, Duplicate }

public sealed record NativeEventDelivery(AutomationEvent Event, NativeEventDisposition Disposition);

/// <summary>Identity and loss detection, independent of the transport. A gap
/// requests a snapshot; it never guesses or applies missing design edits.</summary>
public sealed class NativeEventCursor
{
    private readonly string instanceId;
    private readonly string processEpoch;
    private readonly string eventEpoch;
    private ulong? sequence;
    private bool observed;

    public NativeEventCursor(AutomationSession session, ulong? afterSequence = null)
    {
        if (session.ProtocolVersion != 1 || string.IsNullOrEmpty(session.InstanceId)
            || string.IsNullOrEmpty(session.Epoch) || string.IsNullOrEmpty(session.EventEpoch))
            throw new AutomationException("incompatible_event_stream", "Explicit native event discovery is required.");
        instanceId = session.InstanceId; processEpoch = session.Epoch; eventEpoch = session.EventEpoch;
        sequence = afterSequence;
    }

    public NativeEventDelivery Observe(AutomationEvent notification)
    {
        if (notification.ProtocolVersion != 1)
            throw new AutomationException("incompatible_event_stream", "Unsupported native event protocol.");
        if (notification.InstanceId != instanceId || notification.ProcessEpoch != processEpoch)
            throw new AutomationException("instance_changed", "The event belongs to another native instance or process; reattach explicitly.");
        if (notification.EventEpoch != eventEpoch)
            throw new AutomationException("event_stream_changed", "Native event delivery restarted; rediscover and recover native state.");
        bool change = notification.PayloadCase == AutomationEvent.PayloadOneofCase.SchematicCommit;
        if (!change && notification.PayloadCase != AutomationEvent.PayloadOneofCase.Heartbeat)
            throw new AutomationException("incompatible_event_stream", "Native event has no supported payload.");
        if (change)
        {
            var commit = notification.SchematicCommit;
            if (notification.Sequence == 0 || commit.Document is null || (int)commit.Document.Type != 1
                || commit.Document.Project is null || string.IsNullOrEmpty(commit.Document.Project.Path)
                || commit.Document.SheetPath is null || commit.Document.SheetPath.Path.Count == 0
                || commit.Document.SheetPath.Path.Any(id => !Guid.TryParse(id.Value, out var guid) || guid == Guid.Empty)
                || commit.Revision is null || string.IsNullOrEmpty(commit.Revision.Epoch) || commit.Revision.Sequence == 0
                || commit.Change is null || commit.Change.Sequence != commit.Revision.Sequence
                || (int)commit.Change.Kind is < 1 or > 3)
                throw new AutomationException("invalid_event", "Native change notification is missing its exact document or journal identity.");
        }

        NativeEventDisposition disposition;
        if (sequence is null) disposition = NativeEventDisposition.InitialStateRequired;
        else if (!observed && notification.Sequence < sequence) disposition = NativeEventDisposition.RecoveryRequired;
        else if (notification.Sequence < sequence || (change && notification.Sequence == sequence))
            return new(notification.Clone(), NativeEventDisposition.Duplicate);
        else if (!change && notification.Sequence == sequence) disposition = NativeEventDisposition.Heartbeat;
        else if (change && sequence != ulong.MaxValue && notification.Sequence == sequence + 1)
            disposition = NativeEventDisposition.Change;
        else disposition = NativeEventDisposition.RecoveryRequired;

        observed = true;
        sequence = notification.Sequence;
        return new(notification.Clone(), disposition);
    }
}
