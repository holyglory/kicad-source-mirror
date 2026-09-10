using System.Diagnostics;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

internal sealed record MacUpdateLaunchContext(MacUpdateHandoffRequest Request, InstalledMacUpdate Version,
    string JournalDirectory, string SelectedTarget, string Label, string SocketPath);

/// <summary>One verified native launch path shared by update handoffs and explicit recovery.</summary>
internal static class MacUpdateLauncher
{
    public static async Task<MacUpdateHandoffState> LaunchAsync(MacUpdateLaunchContext context,
        Func<MacUpdateHandoffState, CancellationToken, Task> record, CancellationToken token,
        IReadOnlyDictionary<string, string?>? launchEnvironment = null,
        Action<string, ProcessStartInfo>? beforeLaunch = null, Func<string, Process, Task>? afterLaunch = null)
    {
        var request = context.Request;
        var version = context.Version;
        string root = version.Root, journal = context.JournalDirectory, target = context.SelectedTarget;
        string label = context.Label, socket = context.SocketPath;
        
        var start = new ProcessStartInfo(version.NativeExecutable)
        {
            WorkingDirectory = request.ProjectPath.Length == 0 ? root : Path.GetDirectoryName(request.ProjectPath)!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (launchEnvironment is not null)
            foreach (var (name, value) in launchEnvironment) start.Environment[name] = value;
        start.Environment.Remove("APPDIR");
        start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
        foreach (string variable in new[] { "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH", "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_FALLBACK_FRAMEWORK_PATH", "KICAD_STOCK_DATA_HOME" })
            start.Environment.Remove(variable);
        // The replacement keeps the installed updater identity. Native log
        // redirection closes inherited pipes before it becomes interactive.
        start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = version.McpLauncher;
        start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = version.UpdateConfiguration;
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
            var failure = new MacUpdateHandoffState("launch_failed", journal, target, Error: error.Message);
            await record(failure, CancellationToken.None);
            return failure;
        }
        using (process)
        {
            Task drainOutput = CaptureAsync(process.StandardOutput.BaseStream, Path.Combine(journal, label + ".stdout.log"));
            Task drainError = CaptureAsync(process.StandardError.BaseStream, Path.Combine(journal, label + ".stderr.log"));
            if (afterLaunch is not null) await afterLaunch(label, process);
            // Persist process identity before any readiness wait. If this
            // write fails, do not spawn a second instance or kill this one.
            var launched = new MacUpdateHandoffState("awaiting_native", journal, target, process.Id,
                Endpoint: "ipc://" + socket);
            try
            {
                if (process.HasExited) return await RecordEarlyExit();
                launched = launched with
                {
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    ProcessIdentity = MacProcessIdentity.Read(process.Id)
                };
                // Capture can race with exit even after a successful read.
                if (process.HasExited) return await RecordEarlyExit();
            }
            catch (Exception identityError) when (identityError is IOException or InvalidOperationException
                or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                if (process.HasExited) return await RecordEarlyExit();
                var uncertain = launched with { Status = "reconciliation_required", Error = identityError.Message };
                await record(uncertain, CancellationToken.None);
                return uncertain;
            }
            await record(launched, CancellationToken.None);
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
                        await record(failed, CancellationToken.None);
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
                        await record(ready, CancellationToken.None);
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
                await record(uncertain, CancellationToken.None);
                return uncertain;
            }

            async Task<MacUpdateHandoffState> RecordEarlyExit()
            {
                await Task.WhenAll(drainOutput, drainError);
                var failed = launched with { Status = "launch_failed", Error = "Replacement exited with code " + process.ExitCode };
                await record(failed, CancellationToken.None);
                return failed;
            }
        }
    }

    private static async Task CaptureAsync(Stream source, string path)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(output);
    }
}
