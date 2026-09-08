using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record DesignRecoveryFileObservation(DesignFileChange Change, string? RecoveryRevisionToken,
    bool DesiredBytesChanged, bool ChoicesInvalidated, string? ErrorCode, string? ErrorMessage);

/// <summary>Event-driven intake of saved design bytes into existing recovery state.
/// Subscribes before reading, preserves invalid XML, and never edits the native document,
/// desired design file or baseline. A failed persistence attempt retains its notification
/// so callers can retry after resolving the failure without waiting for another file save.</summary>
public sealed class DesignRecoveryFileObserver : IDisposable
{
    private readonly DesignRecoveryStore store;
    private readonly Guid instanceId;
    private readonly DesignFileSubscription subscription;
    private readonly SemaphoreSlim reader = new(1, 1);
    private DesignFileChange? pending;

    public DesignRecoveryFileObserver(DesignRecoveryStore store, Guid instanceId, string designPath)
    {
        if (instanceId == Guid.Empty || !Path.IsPathFullyQualified(designPath))
            throw new AutomationException("invalid_design_observer", "An explicit instance and absolute design-file path are required.");
        this.store = store; this.instanceId = instanceId;
        _ = ReadRecovery();
        subscription = new DesignFileSubscription(designPath);
    }

    public async Task<DesignRecoveryFileObservation> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        await reader.WaitAsync(cancellationToken);
        try
        {
            pending ??= await subscription.ReceiveAsync(cancellationToken);
            var change = pending;
            if ((change.Reasons & DesignFileChangeReason.ObservationStopped) != 0)
                return new(change, null, false, false, change.ErrorCode ?? "sync_watch_stopped", "Reattach the design-file observer before continuing.");
            try
            {
                var before = ReadRecovery();
                byte[] bytes = await File.ReadAllBytesAsync(subscription.Path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bool changed = !bytes.AsSpan().SequenceEqual(before.State.DesiredFileBytes);
                bool invalidated = changed && before.State.HierarchyResolution is not null;
                if (!changed && store.Read()?.RevisionToken != before.RevisionToken)
                    throw new AutomationException("design_recovery_changed", "Recovery changed during file observation; reload it before continuing.");
                var saved = changed ? store.Save(before.State with { DesiredFileBytes = bytes, HierarchyResolution = null }, before.RevisionToken) : before;
                // Once saved, report the durable result even if cancellation arrives now.
                // The next event captures any subsequent write; this is not an edit lock.
                pending = null;
                string? errorCode = null, errorMessage = null;
                try { _ = DesignRecoveryStore.HierarchySnapshotToken(saved.State); }
                catch (AutomationException error) { errorCode = error.Code; errorMessage = error.Message; }
                return new(change, saved.RevisionToken, changed, invalidated, errorCode, errorMessage);
            }
            catch (AutomationException error)
            { return new(change, null, false, false, error.Code, error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return new(change, null, false, false, "design_file_io", error.Message); }
        }
        finally { reader.Release(); }
    }

    private StoredDesignRecovery ReadRecovery()
    {
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "Create the design recovery record before observing saved changes.");
        if (saved.State.InstanceId != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to a different instance.");
        return saved;
    }

    public void Dispose() => subscription.Dispose();
}
