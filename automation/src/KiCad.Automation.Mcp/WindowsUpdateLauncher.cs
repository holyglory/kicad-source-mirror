using System.Diagnostics;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

internal sealed record WindowsUpdateLaunchContext(WindowsUpdateHandoffRequest Request, VerifiedWindowsVersion Version,
    string JournalDirectory, string SelectionId, string Label, string SocketPath);

internal static class WindowsUpdateLauncher
{
    public static async Task<WindowsUpdateHandoffState> LaunchAsync(WindowsUpdateLaunchContext context,
        Func<WindowsUpdateHandoffState, CancellationToken, Task> record, CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null, Action<string, ProcessStartInfo>? beforeLaunch = null,
        Func<string, Process, Task>? afterLaunch = null)
    {
        var request = context.Request; var version = context.Version;
        string journal = context.JournalDirectory, label = context.Label;
        var start = new ProcessStartInfo(version.NativeExecutable)
        {
            WorkingDirectory = request.ProjectPath.Length == 0 ? version.Root : Path.GetDirectoryName(request.ProjectPath)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (environment is not null) foreach (var (name, value) in environment) start.Environment[name] = value;
        foreach (string name in new[] { "APPDIR", "KICAD_RUN_FROM_BUILD_DIR", "KICAD_AUTOMATION_NNG_LIBRARY" }) start.Environment.Remove(name);
        start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = version.McpExecutable;
        start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = version.UpdateConfiguration;
        foreach (string argument in new[] { "--new", request.ProjectPath.Length == 0 ? "--update-manager" : "--automation", request.InstanceId.ToString("D"),
            "--api-socket", context.SocketPath, "--automation-log", Path.Combine(journal, label + "-native.log") }) start.ArgumentList.Add(argument);
        if (request.SoftwareRendering) start.ArgumentList.Add("--software-rendering");
        if (request.ProjectPath.Length != 0) start.ArgumentList.Add(request.ProjectPath);
        beforeLaunch?.Invoke(label, start);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("The native replacement could not start."); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            var failed = new WindowsUpdateHandoffState("launch_failed", journal, context.SelectionId, Error: error.Message);
            await record(failed, CancellationToken.None); return failed;
        }
        using (process)
        {
            var output = Capture(process.StandardOutput.BaseStream, Path.Combine(journal, label + ".stdout.log"));
            var error = Capture(process.StandardError.BaseStream, Path.Combine(journal, label + ".stderr.log"));
            if (afterLaunch is not null) await afterLaunch(label, process);
            var state = new WindowsUpdateHandoffState("awaiting_native", journal, context.SelectionId, process.Id,
                Endpoint: NativeIpcEndpoint.FromSocketPath(context.SocketPath));
            try
            {
                if (process.HasExited) return await EarlyExit();
                state = state with { ProcessIdentity = WindowsProcessIdentity.Read(process.Id) };
                if (process.HasExited) return await EarlyExit();
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (process.HasExited) return await EarlyExit();
                state = state with { Status = "reconciliation_required", Error = failure.Message };
                await record(state, CancellationToken.None); return state;
            }
            await record(state, CancellationToken.None); // Never spawn another editor if this durable write fails.
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(label == "candidate" ? token : CancellationToken.None);
            readiness.CancelAfter(TimeSpan.FromSeconds(60));
            var client = new NativeClient(new NngTransport(), state.Endpoint!);
            try
            {
                int delay = 25;
                while (true)
                {
                    if (process.HasExited) return await EarlyExit();
                    readiness.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var session = await client.HandshakeAsync(readiness.Token);
                        if (session.InstanceId != request.InstanceId.ToString("D") || session.ProjectPath != request.ProjectPath
                            || WindowsProcessIdentity.Read(process.Id) != state.ProcessIdentity)
                            throw new InvalidDataException("Replacement native/kernel identity differs from the update request.");
                        await Task.WhenAll(output, error).WaitAsync(readiness.Token);
                        var ready = state with { Status = label == "candidate" ? "restarted" : "restored", NativeEpoch = session.Epoch };
                        await record(ready, CancellationToken.None); return ready;
                    }
                    catch (NngException) { }
                    catch (NativeApiException failure) when (failure.Status is 4 or 7) { }
                    await Task.Delay(delay, readiness.Token); delay = Math.Min(delay * 2, 250);
                }
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException or OperationCanceledException
                or KiCad.Automation.Model.AutomationException)
            {
                if (process.HasExited) return await EarlyExit();
                var uncertain = state with { Status = "reconciliation_required", Error = failure.Message };
                await record(uncertain, CancellationToken.None); return uncertain;
            }
            async Task<WindowsUpdateHandoffState> EarlyExit()
            {
                await Task.WhenAll(output, error);
                var failed = state with { Status = "launch_failed", Error = "Replacement exited with code " + process.ExitCode };
                await record(failed, CancellationToken.None); return failed;
            }
        }
    }

    private static async Task Capture(Stream input, string path)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await input.CopyToAsync(output);
    }
}
