using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

/// <summary>Detects accidental drift of a verified, operator-owned payload.
/// This is not an attestation against a malicious owner who can rewrite all
/// local receipts and the installation's publisher policy.</summary>
internal static class LinuxPayloadFingerprint
{
    internal static async Task<string> ComputeAsync(string root, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux payload modes require Linux.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await Visit(new DirectoryInfo(root));
        return Convert.ToHexStringLower(digest.GetHashAndReset());

        async Task Visit(DirectoryInfo directory)
        {
            token.ThrowIfCancellationRequested();
            DateTime modified = directory.LastWriteTimeUtc;
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string path = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is { } link)
                    Append(new(path, "link", 0, null, 0, link));
                else if (entry is DirectoryInfo child)
                {
                    Append(new(path, "directory", 0, null, Mode(child.FullName), null));
                    await Visit(child);
                }
                else if (entry is FileInfo file)
                {
                    long length = file.Length;
                    DateTime changed = file.LastWriteTimeUtc;
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
                    file.Refresh();
                    if (file.Length != length || file.LastWriteTimeUtc != changed)
                        throw new InvalidDataException("The payload changed while its fingerprint was being read.");
                    Append(new(path, "file", length, hash, Mode(file.FullName), null));
                }
                else throw new InvalidDataException("Unrecognized payload entry.");
            }
            directory.Refresh();
            if (directory.LastWriteTimeUtc != modified)
                throw new InvalidDataException("The payload directory changed while being read.");
        }

        void Append(Entry entry)
        {
            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(entry));
            digest.AppendData("\n"u8);
        }
    }

    private static int Mode(string path) => OperatingSystem.IsLinux() ? (int)File.GetUnixFileMode(path)
        : throw new PlatformNotSupportedException();
    private sealed record Entry(string Path, string Kind, long Bytes, string? Sha256, int Mode, string? LinkTarget);
}
