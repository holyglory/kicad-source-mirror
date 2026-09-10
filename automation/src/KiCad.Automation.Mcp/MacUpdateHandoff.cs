using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

public sealed record MacUpdateHandoffRequest(string InstallationRoot, string ExpectedTarget, string ManifestSha256,
    Guid OperationId, MacProcessIdentity OldProcess, string ProjectPath, Guid InstanceId,
    string SocketPath, bool SoftwareRendering, UpdateOrigin? Origin = null);
public sealed record MacUpdateHandoffState(string Status, string JournalDirectory, string? SelectedTarget = null,
    int? ProcessId = null, long? ProcessStartUtcTicks = null, string? NativeEpoch = null, string? Error = null,
    string? Endpoint = null, MacProcessIdentity? ProcessIdentity = null);
public sealed record MacUpdateHandoffIntent(int SchemaVersion, MacUpdateHandoffRequest Request,
    InstalledMacUpdate PreviousVersion, DateTimeOffset PreparedAtUtc,
    IReadOnlyDictionary<string, string?>? LaunchEnvironment = null);

/// <summary>Called only after the operator chooses Update. Waits for a specific
/// existing native process; never closes, signals or kills it. A launch with
/// uncertain readiness remains recorded for reconciliation, not duplicated.</summary>
public static class MacUpdateHandoff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<MacUpdateHandoffState> ExecuteAsync(MacUpdateHandoffRequest request,
        Func<MacUpdateHandoffState, Task> report, CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? launchEnvironment = null) =>
        ExecuteAsync(request, report, launchEnvironment, null, token);

    internal static async Task<MacUpdateHandoffState> ExecuteAsync(MacUpdateHandoffRequest request,
        Func<MacUpdateHandoffState, Task> report, IReadOnlyDictionary<string, string?>? launchEnvironment,
        Action<string, ProcessStartInfo>? beforeLaunch, CancellationToken token,
        Func<string, Process, Task>? afterLaunch = null)
    {
        Validate(request);
        token.ThrowIfCancellationRequested();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallationRoot));
        string journal = Path.Combine(root, "handovers", request.OperationId.ToString("D"));
        string requestPath = Path.Combine(journal, "request.json");
        if (Directory.Exists(journal))
        {
            if (!File.Exists(requestPath)) throw new InvalidDataException("Incomplete handoff journal requires inspection.");
            var prior = JsonSerializer.Deserialize<MacUpdateHandoffRequest>(await File.ReadAllBytesAsync(requestPath, token), Json);
            if (prior != request) throw new InvalidDataException("This restart operation ID belongs to a different request.");
            return new("reconciliation_required", journal, Error: "Inspect the existing handoff before retrying it.");
        }
        if (File.Exists(request.SocketPath) || Directory.Exists(request.SocketPath)
            || File.Exists(request.SocketPath + ".r") || Directory.Exists(request.SocketPath + ".r"))
            throw new ArgumentException("Replacement sockets must not already exist.");
        _ = await MacVerifiedInstallation.InspectActivationAsync(root, request.ExpectedTarget, request.ManifestSha256, token);
        using var old = Process.GetProcessById(request.OldProcess.ProcessId);
        if (old.HasExited || MacProcessIdentity.Read(old.Id) != request.OldProcess)
            throw new InvalidDataException("The old process does not match its kernel start identity.");
        string executable = request.OldProcess.Executable;
        var previousVersion = await MacVerifiedInstallation.InspectExecutableVersionAsync(root, executable, token);
        await UpdateOrigin.VerifyAsync(request.Origin, request.InstanceId, request.ProjectPath, token);
        if (old.HasExited || MacProcessIdentity.Read(old.Id) != request.OldProcess)
            throw new InvalidDataException("The old process changed during version verification.");

        Directory.CreateDirectory(journal);
        using var ownership = new FileStream(Path.Combine(journal, "operation.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(requestPath))
        {
            var prior = JsonSerializer.Deserialize<MacUpdateHandoffRequest>(await File.ReadAllBytesAsync(requestPath, token), Json);
            if (prior != request) throw new InvalidDataException("This restart operation ID belongs to a different request.");
            // A previous execution may have crossed the process launch boundary.
            // Never infer absence of a replacement from a missing final reply.
            return new("reconciliation_required", journal, Error: "Inspect the existing handoff before retrying it.");
        }
        await SaveAsync(requestPath, request, token);
        // The selected installation can differ from this editor's version.
        // Preserve its exact verified identity before permitting the old window
        // to close, so interrupted supervision never has to guess it later.
        await SaveAsync(Path.Combine(journal, "intent.json"),
            new MacUpdateHandoffIntent(2, request, previousVersion, DateTimeOffset.UtcNow,
                UpdateLaunchEnvironment.Capture(launchEnvironment)), token);
        var state = new MacUpdateHandoffState("waiting_for_exit", journal, request.ExpectedTarget);
        await Record(state, token);
        await report(state);
        try { await old.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!old.HasExited)
            {
                state = new("cancelled_before_activation", journal, request.ExpectedTarget);
                await Record(state, CancellationToken.None);
                return state;
            }
            // Cancellation raced with the confirmed close. The activation
            // branch will reject cancellation and restore the verified editor.
        }

        VerifiedMacActivation activated;
        try
        {
            state = new("activating", journal, request.ExpectedTarget);
            await Record(state, token);
            activated = await MacVerifiedInstallation.ActivateAsync(root, request.ExpectedTarget,
                request.ManifestSha256, request.OperationId, token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            state = new("activation_failed", journal,
                MacVerifiedInstallation.InspectTarget(root), Error: error.Message);
            await SaveAsync(Path.Combine(journal, "activation-failure.json"), state, CancellationToken.None);
            await Record(state, CancellationToken.None);
            try
            {
                // The old process is definitively gone and no replacement has
                // been launched. Reverify its retained version, independently
                // of a selection that another project may have changed. Do not
                // roll back that shared selection or the accepted checkpoint.
                var retained = await ReverifyPreviousEditor();
                return await LaunchAsync(retained, state.SelectedTarget!, "restored");
            }
            catch (Exception recoveryError) when (recoveryError is IOException or InvalidDataException or ArgumentException)
            {
                state = new("reconciliation_required", journal, state.SelectedTarget, Error: recoveryError.Message);
                await Record(state, CancellationToken.None);
                return state;
            }
        }
        state = new("launching", journal, activated.Activation.Target);
        await Record(state, CancellationToken.None);
        var candidate = await LaunchAsync(activated.SelectedVersion, activated.Activation.Target, "candidate");
        if (candidate.Status != "launch_failed") return candidate;
        await SaveAsync(Path.Combine(journal, "candidate-failure.json"), candidate, CancellationToken.None);

        // Roll back only after a definite launch failure (no process, or a
        // process observed exited). A still-live uncertain editor is preserved.
        try
        {
            state = new("rolling_back", journal, activated.Activation.Target, Error: candidate.Error);
            await Record(state, CancellationToken.None);
            var rollback = await MacVerifiedInstallation.RollbackAsync(root, activated.Activation.Target,
                request.OperationId, Guid.NewGuid(), CancellationToken.None);
            // Selection rollback belongs to the shared installation; the closed
            // editor may have been running an older retained version. Restore
            // that exact editor without substituting another project's choice.
            var retained = await ReverifyPreviousEditor();
            var recovered = await LaunchAsync(retained, rollback.Activation.Target, "restored");
            return recovered;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            state = new("reconciliation_required", journal, Error: error.Message);
            await Record(state, CancellationToken.None);
            return state;
        }

        async Task<InstalledMacUpdate> ReverifyPreviousEditor()
        {
            var retained = await MacVerifiedInstallation.InspectExecutableVersionAsync(root, executable, CancellationToken.None);
            if (retained != previousVersion)
                throw new InvalidDataException("The previous editor registration changed before recovery.");
            return retained;
        }

        Task<MacUpdateHandoffState> LaunchAsync(InstalledMacUpdate version, string target, string label) =>
            MacUpdateLauncher.LaunchAsync(new(request, version, journal, target, label,
                label == "candidate" ? request.SocketPath : request.SocketPath + ".r"),
                Record, token, launchEnvironment, beforeLaunch, afterLaunch);

        Task Record(MacUpdateHandoffState value, CancellationToken cancellation) => SaveAsync(Path.Combine(journal, "state.json"), value, cancellation);
    }


    private static void Validate(MacUpdateHandoffRequest request)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native update handoff requires macOS.");
        request.Origin?.Validate();
        if (!Path.IsPathFullyQualified(request.InstallationRoot) || request.ProjectPath is null
            || (request.ProjectPath.Length != 0 && (!Path.IsPathFullyQualified(request.ProjectPath)
                || Path.GetExtension(request.ProjectPath) != ".kicad_pro" || !File.Exists(request.ProjectPath)))
            || !Path.IsPathFullyQualified(request.SocketPath) || request.SocketPath.Length > 80
            || request.OldProcess is null || request.OldProcess.ProcessId <= 0 || request.OldProcess.BootId == Guid.Empty
            || request.OldProcess.StartSeconds == 0 || request.OldProcess.StartMicroseconds >= 1_000_000
            || !Path.IsPathFullyQualified(request.OldProcess.Executable)
            || string.IsNullOrEmpty(request.ManifestSha256) || request.ManifestSha256.Length != 64
            || request.ManifestSha256.Any(value => value is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))
            || string.IsNullOrEmpty(request.ExpectedTarget) || !Directory.Exists(Path.GetDirectoryName(request.SocketPath))
            || request.InstanceId == Guid.Empty || request.OperationId == Guid.Empty)
            throw new ArgumentException("Use an exact live process, an existing project or explicit empty manager, and a new short local socket for the handoff.");
    }

    internal static async Task SaveAsync<T>(string path, T value, CancellationToken token, bool overwrite = true)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 65536) throw new InvalidDataException("Update journal record exceeds the supported size.");
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: overwrite);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
