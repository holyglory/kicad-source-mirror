using System.Diagnostics;

namespace KiCad.Automation.Tests;

internal static class WindowsFixtureCleanup
{
    // Call only after all test-owned processes have exited. Windows may release
    // executable mappings after the exit handle is signalled. Do not clear ACLs,
    // attributes, kill other processes or silently accept a leaked directory.
    public static Task RemoveOwnedTemporaryDirectoryAsync(string path, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string absolute = Path.GetFullPath(path);
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetDirectoryName(absolute), temporary, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("kw", StringComparison.Ordinal))
            throw new ArgumentException("Cleanup requires an exact, test-owned kw-prefixed temporary directory.");
        return RemoveAsync(absolute, token);
    }

    public static Task RemoveOwnedRuntimeDirectoryAsync(string path, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "kicad-automation");
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetDirectoryName(absolute), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(absolute), "D", out _))
            throw new ArgumentException("Cleanup requires an exact test-owned native instance runtime directory.");
        return RemoveAsync(absolute, token);
    }

    private static async Task RemoveAsync(string absolute, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        int backoff = 50;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { Directory.Delete(absolute, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception error) when (IsTransient(error) && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(backoff, token);
                backoff = Math.Min(backoff * 2, 1000);
            }
        }
    }

    internal static bool IsTransient(Exception error) => error is IOException or UnauthorizedAccessException
        && (error.HResult & 0xffff) is 5 or 32 or 33;
}
