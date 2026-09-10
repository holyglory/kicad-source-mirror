using System.Diagnostics;

namespace KiCad.Automation.Distribution;

internal static class MacSignatureVerification
{
    internal static async Task VerifyAsync(string payload, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac signature verification requires macOS.");
        var start = new ProcessStartInfo("/usr/bin/codesign") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--verify", "--deep", "--strict",
            Path.Combine(payload, "install/KiCad.app"), Path.Combine(payload, "managed/kicad-mcp") })
            start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(start) ?? throw new IOException("Could not verify registered Mac signatures.");
        Task<string> stdout = MacUpdateStager.ReadBounded(process.StandardOutput, deadline.Token);
        Task<string> stderr = MacUpdateStager.ReadBounded(process.StandardError, deadline.Token);
        Task exit = process.WaitForExitAsync(deadline.Token);
        try
        {
            while (!exit.IsCompleted || !stdout.IsCompleted || !stderr.IsCompleted)
            {
                Task[] pending = new Task[] { exit, stdout, stderr }.Where(x => !x.IsCompleted).ToArray();
                if (pending.Length != 0) await Task.WhenAny(pending);
                if (stdout.IsFaulted || stdout.IsCanceled) await stdout;
                if (stderr.IsFaulted || stderr.IsCanceled) await stderr;
                if (exit.IsFaulted || exit.IsCanceled) await exit;
            }
            await Task.WhenAll(exit, stdout, stderr);
            if (process.ExitCode != 0)
            {
                string diagnostic = await stderr;
                throw new InvalidDataException("Registered Mac signatures changed: " + diagnostic[..Math.Min(512, diagnostic.Length)]);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { }
        }
    }
}
