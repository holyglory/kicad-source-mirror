using KiCad.Automation.Distribution;

namespace KiCad.Automation.Mcp;

public sealed record UpdateRecoveryClaim(int SchemaVersion, Guid OperationId, Guid AttemptId,
    Guid BootId, string SocketPath, DateTimeOffset StartedAtUtc);
public sealed record UpdateRecoveryResult(string Status, Guid OperationId, Guid AttemptId,
    UpdateHandoffState? State = null, bool NativeEditorRestarted = false, bool Reused = false);

/// <summary>Explicit recovery of a closed editor before any replacement launch
/// was reached. Unknown/live/cancelled histories are never guessed or reset.</summary>
public static class LinuxUpdateRecovery
{
    public static Task<UpdateRecoveryResult> RecoverAsync(string installationRoot, Guid operationId,
        Guid attemptId, CancellationToken token = default) =>
        RecoverAsync(installationRoot, operationId, attemptId, null, token);

    internal static async Task<UpdateRecoveryResult> RecoverAsync(string installationRoot, Guid operationId,
        Guid attemptId, Action? beforeLaunch, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Update recovery requires Linux.");
        if (!Path.IsPathFullyQualified(installationRoot) || operationId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("Select an absolute installation, original operation and recovery attempt UUID.");
        token.ThrowIfCancellationRequested();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot));
        string journal = Path.Combine(root, "handovers", operationId.ToString("D"));
        UpdateRecoveryResult Result(string status, UpdateHandoffState? state = null, bool reused = false) =>
            new(status, operationId, attemptId, state, Reused: reused);
        if (!Directory.Exists(journal)) return Result("journal_missing");
        if (new DirectoryInfo(journal).LinkTarget is not null) throw new InvalidDataException("The operation directory is linked.");
        string lockPath = Path.Combine(journal, "operation.lock");
        if (!File.Exists(lockPath)) return Result("journal_incomplete");
        if (new FileInfo(lockPath).LinkTarget is not null) throw new InvalidDataException("The operation lock is linked.");
        FileStream ownership;
        try { ownership = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return Result("operation_unavailable"); }
        using (ownership)
        {
            if (!File.Exists(Path.Combine(journal, "intent.json"))) return Result("legacy_journal_unverifiable");
            var snapshot = await LinuxUpdateInspection.ReadSnapshotAsync(root, journal, operationId, token);
            if (snapshot.Intent.SchemaVersion != 2) return Result("recovery_context_unavailable", snapshot.State);
            var request = snapshot.Intent.Request;
            string executable = Path.Combine(snapshot.PreviousVersion.VersionDirectory, "runtime/bin/kicad");
            string original = LinuxUpdateInspection.ObserveProcess(request.OldProcess, executable);
            if (original == "live") return Result("original_running", snapshot.State);
            if (original is not ("exited" or "identity_changed")) return Result("original_identity_unavailable", snapshot.State);
            string prefix = Path.Combine(journal, "recovery-" + attemptId.ToString("N"));
            if (File.Exists(prefix + ".json"))
                return Result(snapshot.Recovery?.AttemptId == attemptId ? "attempt_already_recorded" : "attempt_not_current",
                    snapshot.State, reused: true);
            if (snapshot.Recovery is not null)
            {
                if (snapshot.State.Status is not ("recovering_previous" or "recovery_cancelled" or "launch_failed"))
                    return Result("recovery_outcome_already_recorded", snapshot.State);
                if (snapshot.State.ProcessId is not null)
                {
                    if (snapshot.State.ProcessIdentity is null) return Result("replacement_identity_unavailable", snapshot.State);
                    string process = LinuxUpdateInspection.ObserveProcess(snapshot.State.ProcessIdentity, executable);
                    if (process is not ("exited" or "identity_changed")) return Result("replacement_may_be_running", snapshot.State);
                }
            }
            else if (snapshot.State.ProcessId is not null
                || snapshot.State.Status is not ("waiting_for_exit" or "activating" or "activation_failed"))
                return Result("launch_outcome_not_recoverable", snapshot.State);
            if (request.ProjectPath.Length != 0 && !File.Exists(request.ProjectPath)) return Result("project_missing", snapshot.State);

            string socket = Path.Combine(Path.GetTempPath(), "kr-" + attemptId.ToString("N")[..12] + ".sock");
            if (socket.Length > 80 || !Directory.Exists(Path.GetDirectoryName(socket)))
                throw new InvalidDataException("Recovery requires an existing short local temporary directory.");
            if (File.Exists(socket) || Directory.Exists(socket) || new FileInfo(socket).LinkTarget is not null)
                return Result("recovery_endpoint_occupied", snapshot.State);
            var claim = new UpdateRecoveryClaim(1, operationId, attemptId,
                LinuxProcessIdentity.Read(Environment.ProcessId).BootId, socket, DateTimeOffset.UtcNow);
            string selection = LinuxUpdateActivation.InspectTarget(Path.Combine(root, "manager"));
            // Preserve history before publishing the active attempt pointer.
            await LinuxUpdateHandoff.SaveAsync(prefix + ".json", claim, token, overwrite: false);
            await LinuxUpdateHandoff.SaveAsync(prefix + ".before.json", snapshot.State, token, overwrite: false);
            var state = new UpdateHandoffState("recovering_previous", journal, selection);
            await LinuxUpdateHandoff.SaveAsync(prefix + ".state.json", state, token, overwrite: false);
            await LinuxUpdateHandoff.SaveAsync(Path.Combine(journal, "recovery.json"), claim, token);
            try
            {
                token.ThrowIfCancellationRequested();
                // Recheck the actual old process immediately before launch; a
                // surviving process must not be duplicated on stale observation.
                original = LinuxUpdateInspection.ObserveProcess(request.OldProcess, executable);
                if (original is not ("exited" or "identity_changed"))
                {
                    state = state with { Status = "recovery_cancelled", Error = "The original process cannot be confirmed closed." };
                    await Record(state, CancellationToken.None);
                    return Result("original_identity_unavailable", state);
                }
                var previous = await LinuxVerifiedInstallation.InspectExecutableVersionAsync(root, executable, token);
                if (previous != snapshot.PreviousVersion) throw new InvalidDataException("The previous version changed before recovery.");
                await Record(state with { Status = "launching" }, token);
                beforeLaunch?.Invoke();
                token.ThrowIfCancellationRequested();
                var result = await LinuxUpdateLauncher.LaunchAsync(new(request, previous, journal, selection,
                    "recovery-" + attemptId.ToString("N"), socket), Record, token, snapshot.Intent.LaunchEnvironment);
                return new(result.Status, operationId, attemptId, result, NativeEditorRestarted: result.Status == "restored");
            }
            catch (OperationCanceledException)
            {
                state = state with { Status = "recovery_cancelled" };
                await Record(state, CancellationToken.None);
                return Result("recovery_cancelled", state);
            }

            async Task Record(UpdateHandoffState value, CancellationToken cancellation)
            {
                // Readers use this attempt's state after the recovery pointer
                // exists, so a crash between these writes cannot select an old
                // top-level state as evidence that no launch happened.
                await LinuxUpdateHandoff.SaveAsync(prefix + ".state.json", value, cancellation);
                await LinuxUpdateHandoff.SaveAsync(Path.Combine(journal, "state.json"), value, cancellation);
            }
        }
    }
}
