using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
                if ((error.HResult & 0xffff) == 5) DeleteReadonlyFixtureHistory(absolute);
                await Task.Delay(backoff, token);
                backoff = Math.Min(backoff * 2, 1000);
            }
        }
    }

    internal static bool IsTransient(Exception error) => error is IOException or UnauthorizedAccessException
        && (error.HResult & 0xffff) is 5 or 32 or 33;

    private static void DeleteReadonlyFixtureHistory(string root)
    {
        string objects = root;
        foreach (string part in new[] { ".history", ".git", "objects" })
        {
            objects = Path.Combine(objects, part);
            if (!Directory.Exists(objects)) return;
            if (File.GetAttributes(objects).HasFlag(FileAttributes.ReparsePoint)) return;
        }
        foreach (string fanout in Directory.EnumerateDirectories(objects))
        {
            if (!Hex(Path.GetFileName(fanout), 2) || File.GetAttributes(fanout).HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (string file in Directory.EnumerateFiles(fanout))
            {
                if (!Hex(Path.GetFileName(file), 38)) continue;
                FileAttributes attributes = File.GetAttributes(file);
                if (!attributes.HasFlag(FileAttributes.ReadOnly) || attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                // Requires DELETE access; no ACL or FileBasicInfo/attribute write.
                // This is the same modern Windows disposition used by the C++ STL.
                using var handle = CreateFileW(file, 0x10000, 7, 0, 3, 0x00200000, 0);
                if (handle.IsInvalid) continue; // Original bounded deletion loop retains failure.
                uint flags = 0x01 | 0x02 | 0x10; // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
                _ = SetFileInformationByHandle(handle, 21, ref flags, sizeof(uint));
            }
        }
        static bool Hex(string value, int length) => value.Length == length && value.All(char.IsAsciiHexDigitLower);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref uint information, uint bytes);
}
