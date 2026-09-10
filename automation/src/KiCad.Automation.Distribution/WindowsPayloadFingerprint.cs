using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

// Detect accidental changes in an operator-owned verified tree. Not an
// attestation against an owner who can replace both receipts and trust policy.
internal static class WindowsPayloadFingerprint
{
    public static async Task<string> ComputeAsync(string root, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Requires native Windows payload semantics.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await Visit(new DirectoryInfo(root));
        return Convert.ToHexStringLower(digest.GetHashAndReset());

        async Task Visit(DirectoryInfo directory)
        {
            token.ThrowIfCancellationRequested();
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("A registered Windows payload cannot contain redirected directories.");
            DateTime modified = directory.LastWriteTimeUtc;
            foreach (var item in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("A registered Windows payload cannot contain links or reparse points.");
                string name = Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
                if (item is DirectoryInfo child) { Append(new(name, true, 0, null)); await Visit(child); }
                else if (item is FileInfo file)
                {
                    DateTime changed = file.LastWriteTimeUtc; long length = file.Length;
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
                    file.Refresh();
                    if (file.Length != length || file.LastWriteTimeUtc != changed) throw new InvalidDataException("Windows payload bytes changed during verification.");
                    Append(new(name, false, length, hash));
                }
                else throw new InvalidDataException("Unrecognized Windows payload entry.");
            }
            directory.Refresh();
            if (directory.LastWriteTimeUtc != modified) throw new InvalidDataException("Windows payload directory changed during verification.");
        }
        void Append(Entry entry) { digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(entry)); digest.AppendData("\n"u8); }
    }
    private sealed record Entry(string Path, bool Directory, long Bytes, string? Sha256);
}
