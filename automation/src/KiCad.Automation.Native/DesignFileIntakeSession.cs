using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum DesignFileIntakePhase { Starting, Watching, InvalidDesign, Paused, Stopped }
public sealed record DesignFileIntakeStatus(ulong Sequence, DesignFileIntakePhase Phase,
    DesignRecoveryFileObservation? Observation, string? ErrorCode, string? ErrorMessage);

/// <summary>Owns one continuous file-to-recovery intake task, not a native synchronization
/// worker. Waiters use versioned events; persistence failures pause until explicit resume.
/// Invalid XML stays preserved and watched so a later corrected save can recover normally.</summary>
public sealed class DesignFileIntakeSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly DesignRecoveryFileObserver observer;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private TaskCompletionSource changed = Signal();
    private TaskCompletionSource? resume;
    private DesignFileIntakeStatus status = new(0, DesignFileIntakePhase.Starting, null, null, null);

    public DesignFileIntakeSession(DesignRecoveryStore store, Guid instanceId, string designPath)
    {
        observer = new(store, instanceId, designPath);
        worker = Task.Run(RunAsync);
    }

    public DesignFileIntakeStatus Inspect() { lock (gate) return status; }

    public async Task<DesignFileIntakeStatus> WaitAsync(ulong afterSequence, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (afterSequence > status.Sequence)
                    throw new AutomationException("invalid_intake_cursor", "The requested cursor is ahead of this intake session.");
                if (status.Sequence > afterSequence || status.Phase == DesignFileIntakePhase.Stopped) return status;
                next = changed.Task;
            }
            await next.WaitAsync(cancellationToken);
        }
    }

    public void Resume(ulong expectedSequence)
    {
        lock (gate)
        {
            if (status.Sequence != expectedSequence || status.Phase != DesignFileIntakePhase.Paused || resume is null)
                throw new AutomationException("intake_state_changed", "Resume only the currently paused intake status.");
            if (status.Observation?.Change.Reasons.HasFlag(DesignFileChangeReason.ObservationStopped) == true)
                throw new AutomationException("intake_reattach_required", "The file watch stopped; create a new intake session after repairing the directory.");
            resume.TrySetResult();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                stopping.Token.ThrowIfCancellationRequested();
                var observation = await observer.ReceiveAsync(stopping.Token);
                if (observation.RecoveryRevisionToken is null)
                {
                    Task restart;
                    lock (gate)
                    {
                        resume = Signal(); restart = resume.Task;
                        Publish(DesignFileIntakePhase.Paused, observation, observation.ErrorCode, observation.ErrorMessage);
                    }
                    await restart.WaitAsync(stopping.Token);
                    lock (gate) resume = null;
                }
                else
                {
                    lock (gate) Publish(observation.ErrorCode is null ? DesignFileIntakePhase.Watching : DesignFileIntakePhase.InvalidDesign,
                        observation, observation.ErrorCode, observation.ErrorMessage);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) Publish(DesignFileIntakePhase.Stopped, status.Observation,
                error is AutomationException a ? a.Code : "intake_failed", error.Message);
        }
        finally
        {
            observer.Dispose();
            lock (gate)
                if (status.Phase != DesignFileIntakePhase.Stopped)
                    Publish(DesignFileIntakePhase.Stopped, status.Observation, null, null);
        }
    }

    // Caller holds gate; continuations never run inside it.
    private void Publish(DesignFileIntakePhase phase, DesignRecoveryFileObservation? observation, string? code, string? message)
    {
        status = new(checked(status.Sequence + 1), phase, observation, code, message);
        var previous = changed; changed = Signal(); previous.TrySetResult();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        // Await the owned task: returning proves there can be no later recovery write.
        await stopping.CancelAsync();
        await worker;
    }
}
