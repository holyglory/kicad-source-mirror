using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Mcp;

public static class WindowsLaunchCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter errors, CancellationToken token)
    {
        string? root = null;
        bool bootstrapVerified = false;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Windows launch requires Windows.");
            if (args.Length < 6 || args[0] != "--launch-installed" || args[1] != "--installation"
                || !Path.IsPathFullyQualified(args[2]) || args[3] != "--target" || args[4] is not ("native" or "mcp") || args[5] != "--")
                throw new ArgumentException("Use --launch-installed --installation ABSOLUTE_ROOT --target native|mcp -- [arguments].");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2]));
            await WindowsVerifiedVersions.ValidateBootstrapAsync(root, token);
            bootstrapVerified = true;
            var selected = await WindowsVerifiedVersions.InspectCurrentAsync(root, token);
            var start = StartInfo(selected.Version, args[4], args[6..]);
            token.ThrowIfCancellationRequested();
            if (WindowsVerifiedVersions.InspectSelectionId(root) != selected.SelectionId)
                throw new InvalidDataException("The Windows selection changed during launch verification; retry the launch.");
            using var process = Process.Start(start) ?? throw new IOException("Windows could not start the selected application.");
            if (args[4] == "native") return 0; // Process creation is not an editor-readiness or update qualification claim.
            try { await process.WaitForExitAsync(token); return process.ExitCode; }
            catch (OperationCanceledException)
            {
                // Stop only this MCP server, never its independent dirty editors.
                if (!process.HasExited) process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(CancellationToken.None); throw;
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException or Win32Exception
            or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException
            or PlatformNotSupportedException or OperationCanceledException)
        {
            string message = JsonSerializer.Serialize(new { schemaVersion = 1, status = error is OperationCanceledException ? "cancelled" : "failed",
                nativeEditorReady = false, error = new { kind = error.GetType().Name, message = error.Message } });
            await errors.WriteLineAsync(message); await errors.FlushAsync(CancellationToken.None);
            // Only a validated installation may receive diagnostics. Do not create
            // arbitrary paths merely because malformed input supplied a root.
            if (bootstrapVerified && root is not null)
            {
                try
                {
                    string directory = Directory.CreateDirectory(Path.Combine(root, "launch-errors")).FullName;
                    if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("Launch diagnostics directory cannot be redirected.");
                    await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), message, CancellationToken.None);
                }
                catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException) { }
            }
            return error is OperationCanceledException ? 2 : 1;
        }
    }

    internal static ProcessStartInfo StartInfo(VerifiedWindowsVersion version, string target, string[] arguments)
    {
        if (target is not ("native" or "mcp")) throw new ArgumentException("Unknown Windows launch target.");
        var start = new ProcessStartInfo(target == "native" ? version.NativeExecutable : version.McpExecutable)
        { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string key in new[] { "KICAD_AUTOMATION_NNG_LIBRARY", "KICAD_RUN_FROM_BUILD_DIR", "APPDIR" }) start.Environment.Remove(key);
        start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = version.McpExecutable;
        start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = version.UpdateConfiguration;
        return start;
    }
}
