using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

public sealed record LinuxUpdateHandoffRequest(string InstallationRoot, string ExpectedTarget, string ManifestSha256,
    Guid OperationId, LinuxProcessIdentity OldProcess, string ProjectPath, Guid InstanceId,
    string SocketPath, bool SoftwareRendering);
public sealed record UpdateHandoffState(string Status, string JournalDirectory, string? SelectedTarget = null,
    int? ProcessId = null, long? ProcessStartUtcTicks = null, string? NativeEpoch = null, string? Error = null,
    string? Endpoint = null, LinuxProcessIdentity? ProcessIdentity = null);
public sealed record UpdateHandoffIntent(int SchemaVersion, LinuxUpdateHandoffRequest Request,
    InstalledLinuxUpdate PreviousVersion, DateTimeOffset PreparedAtUtc);

/// <summary>Called only after the operator chooses Update. Waits for a specific
/// existing native process; never closes, signals or kills it. A launch with
/// uncertain readiness remains recorded for reconciliation, not duplicated.</summary>
public static class LinuxUpdateHandoff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<UpdateHandoffState> ExecuteAsync(LinuxUpdateHandoffRequest request,
        Func<UpdateHandoffState, Task> report, CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? launchEnvironment = null) =>
        ExecuteAsync(request, report, launchEnvironment, null, token);

    internal static async Task<UpdateHandoffState> ExecuteAsync(LinuxUpdateHandoffRequest request,
        Func<UpdateHandoffState, Task> report, IReadOnlyDictionary<string, string?>? launchEnvironment,
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
            var prior = JsonSerializer.Deserialize<LinuxUpdateHandoffRequest>(await File.ReadAllBytesAsync(requestPath, token), Json);
            if (prior != request) throw new InvalidDataException("This restart operation ID belongs to a different request.");
            return new("reconciliation_required", journal, Error: "Inspect the existing handoff before retrying it.");
        }
        if (File.Exists(request.SocketPath) || Directory.Exists(request.SocketPath)
            || File.Exists(request.SocketPath + ".r") || Directory.Exists(request.SocketPath + ".r"))
            throw new ArgumentException("Replacement sockets must not already exist.");
        _ = await LinuxVerifiedInstallation.InspectActivationAsync(root, request.ExpectedTarget, request.ManifestSha256, token);
        using var old = Process.GetProcessById(request.OldProcess.ProcessId);
        if (old.HasExited || LinuxProcessIdentity.Read(old.Id) != request.OldProcess)
            throw new InvalidDataException("The old process does not match its kernel start identity.");
        string executable = old.MainModule?.FileName ?? throw new InvalidDataException("The old executable identity is unavailable.");
        var previousVersion = await LinuxVerifiedInstallation.InspectExecutableVersionAsync(root, executable, token);
        if (old.HasExited || LinuxProcessIdentity.Read(old.Id) != request.OldProcess || old.MainModule?.FileName != executable)
            throw new InvalidDataException("The old process changed during version verification.");

        Directory.CreateDirectory(journal);
        using var ownership = new FileStream(Path.Combine(journal, "operation.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(requestPath))
        {
            var prior = JsonSerializer.Deserialize<LinuxUpdateHandoffRequest>(await File.ReadAllBytesAsync(requestPath, token), Json);
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
            new UpdateHandoffIntent(1, request, previousVersion, DateTimeOffset.UtcNow), token);
        var state = new UpdateHandoffState("waiting_for_exit", journal, request.ExpectedTarget);
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

        VerifiedLinuxActivation activated;
        try
        {
            state = new("activating", journal, request.ExpectedTarget);
            await Record(state, token);
            activated = await LinuxVerifiedInstallation.ActivateAsync(root, request.ExpectedTarget,
                request.ManifestSha256, request.OperationId, token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            state = new("activation_failed", journal,
                LinuxUpdateActivation.InspectTarget(Path.Combine(root, "manager")), Error: error.Message);
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
            var rollback = await LinuxVerifiedInstallation.RollbackAsync(root, activated.Activation.Target,
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

        async Task<InstalledLinuxUpdate> ReverifyPreviousEditor()
        {
            var retained = await LinuxVerifiedInstallation.InspectExecutableVersionAsync(root, executable, CancellationToken.None);
            if (retained != previousVersion)
                throw new InvalidDataException("The previous editor registration changed before recovery.");
            return retained;
        }

        async Task<UpdateHandoffState> LaunchAsync(InstalledLinuxUpdate version, string target, string label)
        {
            string prefix = Path.Combine(version.VersionDirectory, "runtime");
            string socket = label == "candidate" ? request.SocketPath : request.SocketPath + ".r";
            var start = new ProcessStartInfo(Path.Combine(prefix, "bin/kicad"))
            {
                WorkingDirectory = request.ProjectPath.Length == 0 ? root : Path.GetDirectoryName(request.ProjectPath)!, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (launchEnvironment is not null)
                foreach (var (name, value) in launchEnvironment) start.Environment[name] = value;
            start.Environment.Remove("APPDIR");
            start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
            start.Environment["LD_LIBRARY_PATH"] = Path.Combine(prefix, "lib");
            start.Environment["KICAD_STOCK_DATA_HOME"] = Path.Combine(prefix, "share/kicad");
            // The replacement keeps the installed updater identity. Native log
            // redirection closes inherited pipes before it becomes interactive.
            start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = Path.Combine(prefix, "lib/kicad-automation/kicad-mcp");
            start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = version.ConfigurationPath;
            foreach (string argument in new[] { "--new", request.ProjectPath.Length == 0 ? "--update-manager" : "--automation", request.InstanceId.ToString("D"),
                "--api-socket", socket, "--automation-log", Path.Combine(journal, label + "-native.log") })
                start.ArgumentList.Add(argument);
            if (request.SoftwareRendering) start.ArgumentList.Add("--software-rendering");
            if (request.ProjectPath.Length != 0) start.ArgumentList.Add(request.ProjectPath);
            beforeLaunch?.Invoke(label, start);
            Process process;
            try { process = Process.Start(start) ?? throw new IOException("Replacement did not start."); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            {
                var failure = new UpdateHandoffState("launch_failed", journal, target, Error: error.Message);
                await Record(failure, CancellationToken.None);
                return failure;
            }
            using (process)
            {
                Task drainOutput = CaptureAsync(process.StandardOutput.BaseStream, Path.Combine(journal, label + ".stdout.log"));
                Task drainError = CaptureAsync(process.StandardError.BaseStream, Path.Combine(journal, label + ".stderr.log"));
                if (afterLaunch is not null) await afterLaunch(label, process);
                // Persist process identity before any readiness wait. If this
                // write fails, do not spawn a second instance or kill this one.
                var launched = new UpdateHandoffState("awaiting_native", journal, target, process.Id,
                    Endpoint: "ipc://" + socket);
                try
                {
                    if (process.HasExited) return await RecordEarlyExit();
                    launched = launched with
                    {
                        ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        ProcessIdentity = LinuxProcessIdentity.Read(process.Id)
                    };
                    // Capture can race with exit even after a successful read.
                    if (process.HasExited) return await RecordEarlyExit();
                }
                catch (Exception identityError) when (identityError is IOException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    if (process.HasExited) return await RecordEarlyExit();
                    var uncertain = launched with { Status = "reconciliation_required", Error = identityError.Message };
                    await Record(uncertain, CancellationToken.None);
                    return uncertain;
                }
                await Record(launched, CancellationToken.None);
                // Once the old editor has closed, restoring it is bounded
                // cleanup even if the update request itself was cancelled.
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(
                    label == "candidate" ? token : CancellationToken.None);
                readiness.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    var client = new NativeClient(new NngTransport(), "ipc://" + socket);
                    while (true)
                    {
                        if (process.HasExited)
                        {
                            await Task.WhenAll(drainOutput, drainError);
                            var failed = launched with { Status = "launch_failed", Error = "Replacement exited with code " + process.ExitCode };
                            await Record(failed, CancellationToken.None);
                            return failed;
                        }
                        readiness.Token.ThrowIfCancellationRequested();
                        try
                        {
                            var session = await client.HandshakeAsync(readiness.Token);
                            if (session.InstanceId != request.InstanceId.ToString("D") || session.ProjectPath != request.ProjectPath)
                                throw new InvalidDataException("Replacement instance identity does not match the handoff.");
                            // Native --automation-log must release both inherited
                            // pipes before the helper can finish independently.
                            await Task.WhenAll(drainOutput, drainError).WaitAsync(readiness.Token);
                            var ready = launched with { Status = label == "candidate" ? "restarted" : "restored", NativeEpoch = session.Epoch };
                            await Record(ready, CancellationToken.None);
                            return ready;
                        }
                        catch (NngException) { }
                        catch (NativeApiException error) when (error.Status is 4 or 7) { }
                        await Task.Delay(100, readiness.Token);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException
                    or KiCad.Automation.Model.AutomationException)
                {
                    var uncertain = launched with { Status = "reconciliation_required", Error = error.Message };
                    await Record(uncertain, CancellationToken.None);
                    return uncertain;
                }

                async Task<UpdateHandoffState> RecordEarlyExit()
                {
                    await Task.WhenAll(drainOutput, drainError);
                    var failed = launched with { Status = "launch_failed", Error = "Replacement exited with code " + process.ExitCode };
                    await Record(failed, CancellationToken.None);
                    return failed;
                }
            }
        }

        Task Record(UpdateHandoffState value, CancellationToken cancellation) => SaveAsync(Path.Combine(journal, "state.json"), value, cancellation);
    }

    private static async Task CaptureAsync(Stream source, string path)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(output);
    }

    private static void Validate(LinuxUpdateHandoffRequest request)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native update handoff requires Linux.");
        if (!Path.IsPathFullyQualified(request.InstallationRoot) || request.ProjectPath is null
            || (request.ProjectPath.Length != 0 && (!Path.IsPathFullyQualified(request.ProjectPath)
                || Path.GetExtension(request.ProjectPath) != ".kicad_pro" || !File.Exists(request.ProjectPath)))
            || !Path.IsPathFullyQualified(request.SocketPath) || request.SocketPath.Length > 80
            || request.OldProcess is null || request.OldProcess.ProcessId <= 0 || request.OldProcess.BootId == Guid.Empty
            || string.IsNullOrEmpty(request.ManifestSha256) || request.ManifestSha256.Length != 64
            || request.ManifestSha256.Any(value => value is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))
            || string.IsNullOrEmpty(request.ExpectedTarget) || !Directory.Exists(Path.GetDirectoryName(request.SocketPath))
            || request.InstanceId == Guid.Empty || request.OperationId == Guid.Empty)
            throw new ArgumentException("Use an exact live process, an existing project or explicit empty manager, and a new short local socket for the handoff.");
    }

    private static async Task SaveAsync<T>(string path, T value, CancellationToken token)
    {
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, value, Json, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
