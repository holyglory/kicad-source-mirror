using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KiCad.Automation.Distribution;

public sealed record LinuxUpdateRuntimeResult(string Commit, string Directory, string ManifestSha256);

/// <summary>Runs the staged native CLI in isolated configuration and checks
/// its compiled commit. This is not a rendering, installation or restart proof.</summary>
public static class LinuxUpdateRuntime
{
    public static async Task<LinuxUpdateRuntimeResult> CheckAsync(VerifiedUpdateManifest manifest,
        StagedLinuxUpdate staged, string scratchRoot, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Linux x64 native runtime check requires that runtime target.");
        if (staged.ManifestSha256 != manifest.PayloadSha256)
            throw new InvalidDataException("The staged candidate belongs to a different update release.");
        if (!Path.IsPathFullyQualified(scratchRoot) || !Directory.Exists(scratchRoot))
            throw new ArgumentException("Use an existing dedicated runtime-check scratch directory.", nameof(scratchRoot));
        cancellationToken.ThrowIfCancellationRequested();
        string scratch = Path.Combine(scratchRoot, "runtime-check-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(scratch) || File.Exists(scratch)) throw new IOException("Runtime-check destination already exists.");
        Directory.CreateDirectory(scratch);
        try
        {
            string prefix = Path.Combine(staged.Directory, "runtime");
            var start = new ProcessStartInfo(Path.Combine(prefix, "bin", "kicad-cli"))
            {
                WorkingDirectory = scratch, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "version", "--format", "commit" }) start.ArgumentList.Add(argument);
            start.Environment.Remove("APPDIR");
            start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
            start.Environment["LD_LIBRARY_PATH"] = Path.Combine(prefix, "lib");
            start.Environment["KICAD_STOCK_DATA_HOME"] = Path.Combine(prefix, "share", "kicad");
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(scratch, "config");
            start.Environment["KICAD_CACHE_HOME"] = Path.Combine(scratch, "cache");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "xdg-config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(scratch, "xdg-cache");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var process = Process.Start(start) ?? throw new IOException("The staged native CLI did not start.");
            Task<string> stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            Task<string> stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            try
            {
                // A stream limit or read error must stop its owned child rather
                // than leaving it blocked writing into a full redirected pipe.
                var exit = process.WaitForExitAsync(deadline.Token);
                while (!exit.IsCompleted || !stdout.IsCompleted || !stderr.IsCompleted)
                {
                    if (stdout.IsFaulted || stdout.IsCanceled) await stdout;
                    if (stderr.IsFaulted || stderr.IsCanceled) await stderr;
                    if (exit.IsFaulted || exit.IsCanceled) await exit;
                    Task[] pending = new Task[] { exit, stdout, stderr }.Where(task => !task.IsCompleted).ToArray();
                    if (pending.Length == 0) break;
                    await Task.WhenAny(pending);
                    if (stdout.IsFaulted || stdout.IsCanceled) await stdout;
                    if (stderr.IsFaulted || stderr.IsCanceled) await stderr;
                    if (exit.IsFaulted || exit.IsCanceled) await exit;
                }
                await Task.WhenAll(exit, stdout, stderr);
                string commit = (await stdout).Trim();
                if (process.ExitCode != 0)
                {
                    string diagnostic = (await stderr).Trim();
                    throw new InvalidDataException("The staged KiCad CLI exited with code " + process.ExitCode + ": "
                        + diagnostic[..Math.Min(diagnostic.Length, 512)]);
                }
                if (commit != manifest.Release.Commit)
                    throw new InvalidDataException("The staged KiCad CLI does not report the signed source commit.");
                return new(commit, staged.Directory, manifest.PayloadSha256);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { }
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        char[] buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        {
            if (count > 32 * 1024 - text.Length) throw new InvalidDataException("Native runtime-check output exceeds its limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
