using System.Diagnostics;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Mcp;

public sealed record WindowsUpdateRecoveryClaim(int SchemaVersion, Guid OperationId, Guid AttemptId, string SocketPath, DateTimeOffset StartedAtUtc);
public sealed record WindowsUpdateRecoveryResult(string Status, Guid OperationId, Guid AttemptId,
    WindowsUpdateHandoffState? State = null, bool NativeEditorRestarted = false, bool Reused = false);

/// <summary>Explicit recovery only when persisted state proves a replacement
/// launch was not reached. Never guesses that a missing reply means no editor.</summary>
public static class WindowsUpdateRecovery
{
    public static Task<WindowsUpdateRecoveryResult> RecoverAsync(string root, Guid operationId, Guid attemptId, CancellationToken token = default) =>
        RecoverAsync(root, operationId, attemptId, null, token);

    internal static async Task<WindowsUpdateRecoveryResult> RecoverAsync(string installationRoot, Guid operationId, Guid attemptId,
        Func<string, Process, Task>? afterLaunch, CancellationToken token)
    {
        string root = WindowsUpdateInspection.Root(installationRoot, operationId);
        if (attemptId == Guid.Empty) throw new ArgumentException("Use an exact recovery attempt UUID.");
        token.ThrowIfCancellationRequested();
        string journal = Path.Combine(root, "handovers", operationId.ToString("D"));
        WindowsUpdateRecoveryResult Result(string status, WindowsUpdateHandoffState? state = null, bool reused = false) =>
            new(status, operationId, attemptId, state, Reused: reused);
        if (!Directory.Exists(journal)) return Result("journal_missing");
        WindowsUpdateInspection.Ordinary(root); WindowsUpdateInspection.Ordinary(Path.Combine(root, "handovers"));
        WindowsUpdateInspection.Ordinary(journal);
        string lockPath = Path.Combine(journal, "operation.lock");
        if (!File.Exists(lockPath)) return Result("journal_incomplete");
        WindowsUpdateInspection.Ordinary(lockPath);
        FileStream ownership;
        try { ownership = new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return Result("operation_unavailable"); }
        using (ownership)
        {
            if (!File.Exists(Path.Combine(journal, "intent.json"))) return Result("legacy_journal_unverifiable");
            var snapshot = await WindowsUpdateInspection.ReadSnapshotAsync(root, journal, operationId, token);
            var request = snapshot.Intent.Request;
            string original = WindowsObservedProcess.Observe(request.OldProcess, snapshot.PreviousVersion.NativeExecutable);
            if (original == "live") return Result("original_running", snapshot.State);
            if (original is not ("exited" or "identity_changed")) return Result("original_identity_unavailable", snapshot.State);
            string prefix = Path.Combine(journal, "recovery-" + attemptId.ToString("N"));
            if (File.Exists(prefix + ".json"))
                return Result(snapshot.Recovery?.AttemptId == attemptId ? "attempt_already_recorded" : "attempt_not_current", snapshot.State, true);
            if (snapshot.Recovery is not null)
            {
                if (snapshot.State.Status is not ("recovering_previous" or "recovery_cancelled" or "launch_failed"))
                    return Result("recovery_outcome_already_recorded", snapshot.State);
                if (snapshot.State.ProcessId is not null)
                {
                    if (snapshot.State.ProcessIdentity is null) return Result("replacement_identity_unavailable", snapshot.State);
                    if (WindowsObservedProcess.Observe(snapshot.State.ProcessIdentity, snapshot.PreviousVersion.NativeExecutable) is not ("exited" or "identity_changed"))
                        return Result("replacement_may_be_running", snapshot.State);
                }
            }
            else if (snapshot.State.ProcessId is not null || snapshot.State.Status is not ("waiting_for_exit" or "activating" or "activation_failed"))
                return Result("launch_outcome_not_recoverable", snapshot.State);
            if (request.ProjectPath.Length != 0 && !File.Exists(request.ProjectPath)) return Result("project_missing", snapshot.State);
            // Windows NNG uses this as a local named-pipe name, not a filesystem
            // file. No directory or file is created at the drive root.
            string socket = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "kwr-" + attemptId.ToString("N") + ".sock");
            var claim = new WindowsUpdateRecoveryClaim(1, operationId, attemptId, socket, DateTimeOffset.UtcNow);
            string selection = WindowsVerifiedVersions.InspectSelectionId(root);
            await WindowsUpdateHandoff.SaveAsync(prefix + ".json", claim, token, overwrite: false);
            await WindowsUpdateHandoff.SaveAsync(prefix + ".before.json", snapshot.State, token, overwrite: false);
            var state = new WindowsUpdateHandoffState("recovering_previous", journal, selection);
            await WindowsUpdateHandoff.SaveAsync(prefix + ".state.json", state, token, overwrite: false);
            await WindowsUpdateHandoff.SaveAsync(Path.Combine(journal, "recovery.json"), claim, token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (WindowsObservedProcess.Observe(request.OldProcess, snapshot.PreviousVersion.NativeExecutable) is not ("exited" or "identity_changed"))
                {
                    state = state with { Status = "recovery_cancelled", Error = "Original process is not confirmed closed." };
                    await Record(state, CancellationToken.None);
                    return Result("original_identity_unavailable", state);
                }
                var previous = await WindowsVerifiedVersions.InspectExecutableAsync(root, snapshot.PreviousVersion.NativeExecutable, token);
                if (previous != snapshot.PreviousVersion) throw new InvalidDataException("The retained Windows editor changed before recovery.");
                await Record(state with { Status = "launching" }, token);
                token.ThrowIfCancellationRequested();
                var result = await WindowsUpdateLauncher.LaunchAsync(new(request, previous, journal, selection,
                    "recovery-" + attemptId.ToString("N"), socket), Record, token, snapshot.Intent.LaunchEnvironment, afterLaunch: afterLaunch);
                return new(result.Status, operationId, attemptId, result, NativeEditorRestarted: result.Status == "restored");
            }
            catch (OperationCanceledException)
            {
                state = state with { Status = "recovery_cancelled" }; await Record(state, CancellationToken.None);
                return Result(state.Status, state);
            }
            async Task Record(WindowsUpdateHandoffState value, CancellationToken cancellation)
            {
                await WindowsUpdateHandoff.SaveAsync(prefix + ".state.json", value, cancellation);
                await WindowsUpdateHandoff.SaveAsync(Path.Combine(journal, "state.json"), value, cancellation);
            }
        }
    }
}
