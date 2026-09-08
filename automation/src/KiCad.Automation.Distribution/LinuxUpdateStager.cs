using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

public sealed class StagedLinuxUpdate
{
    public string Directory { get; }
    public string ManifestSha256 { get; }
    internal StagedLinuxUpdate(string directory, string digest) { Directory = directory; ManifestSha256 = digest; }
}

/// <summary>Prepares a verified Linux archive in a new directory only. This
/// neither replaces an installation nor proves native runtime compatibility.</summary>
public static class LinuxUpdateStager
{
    public static Task<StagedLinuxUpdate> StageAsync(VerifiedUpdateManifest manifest, DownloadedUpdate download,
        string stagingRoot, CancellationToken cancellationToken = default) =>
        StageAsync(manifest, download, stagingRoot, 64L * 1024 * 1024 * 1024, 200_000, cancellationToken);

    internal static async Task<StagedLinuxUpdate> StageAsync(VerifiedUpdateManifest manifest, DownloadedUpdate download,
        string stagingRoot, long maximumExpandedBytes, int maximumEntries, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux archive staging requires Linux.");
        if (!Path.IsPathFullyQualified(stagingRoot) || !Directory.Exists(stagingRoot))
            throw new ArgumentException("Use an existing dedicated update staging directory.", nameof(stagingRoot));
        if (maximumExpandedBytes < 1 || maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumExpandedBytes));
        var artifact = manifest.ForInstallation("linux-x64", "tar.gz");
        if (artifact is null || artifact != download.Artifact || manifest.PayloadSha256 != download.ManifestSha256)
            throw new InvalidDataException("The downloaded archive does not belong to this update manifest.");
        cancellationToken.ThrowIfCancellationRequested();
        await using var archive = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (archive.Length != artifact.Bytes
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, cancellationToken)) != artifact.Sha256)
            throw new InvalidDataException("Downloaded update bytes changed before staging.");
        archive.Position = 0;

        string work = Path.Combine(stagingRoot, "stage-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Update staging destination already exists.");
        Directory.CreateDirectory(work);
        string payload = Directory.CreateDirectory(Path.Combine(work, "payload")).FullName;
        try
        {
            var entries = new Dictionary<string, TarEntryType>(StringComparer.Ordinal);
            var links = new Dictionary<string, string>(StringComparer.Ordinal);
            long expanded = 0;
            await using (var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true))
            await using (var tar = new TarReader(gzip, leaveOpen: true))
            {
                while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = CanonicalName(entry.Name, entry.EntryType == TarEntryType.Directory);
                    if (entries.Count >= maximumEntries || !entries.TryAdd(name, entry.EntryType))
                        throw new InvalidDataException("The update archive has too many entries or a duplicate path.");
                    string path = Path.Combine(payload, name);
                    if (entry.Length < 0 || entry.Length > maximumExpandedBytes - expanded)
                        throw new InvalidDataException("The update archive exceeds its expanded size limit.");
                    expanded += entry.Length;
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory:
                            Directory.CreateDirectory(path);
                            break;
                        case TarEntryType.RegularFile:
                        case TarEntryType.V7RegularFile:
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, cancellationToken);
                                if (output.Length != entry.Length) throw new InvalidDataException("Truncated update archive entry.");
                                await output.FlushAsync(cancellationToken);
                                output.Flush(flushToDisk: true);
                            }
                            // Keep ordinary executable/data permissions, never set-id or sticky bits.
                            File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0x1FF);
                            break;
                        case TarEntryType.SymbolicLink:
                            links.Add(name, ResolveLinkName(name, entry.LinkName));
                            break;
                        default:
                            throw new InvalidDataException("Unsupported update archive entry type: " + entry.EntryType);
                    }
                }
                // Consume gzip's trailer and reject hidden entries after tar's
                // end marker. Normal tar record padding is zero-filled.
                byte[] padding = new byte[8192];
                int count;
                long paddingBytes = 0;
                while ((count = await gzip.ReadAsync(padding, cancellationToken)) != 0)
                {
                    paddingBytes += count;
                    if (paddingBytes > 1024 * 1024 || padding.AsSpan(0, count).ContainsAnyExcept((byte)0))
                        throw new InvalidDataException("Unexpected data after the update archive end marker.");
                }
            }

            // Links are created only after all ordinary writes. No archive path
            // may write through a link, and every link must resolve to a bundled
            // regular file. Linux bundles do not need directory or hard links.
            foreach (var (name, target) in links)
            {
                string resolved = target;
                var visited = new HashSet<string>(StringComparer.Ordinal) { name };
                while (links.TryGetValue(resolved, out string? next))
                {
                    if (!visited.Add(resolved)) throw new InvalidDataException("Cyclic update archive link.");
                    resolved = next;
                }
                if (!entries.TryGetValue(resolved, out var type)
                    || type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Update archive link does not resolve to a bundled regular file.");
                string path = Path.Combine(payload, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.CreateSymbolicLink(path, Path.GetRelativePath(Path.GetDirectoryName(path)!, Path.Combine(payload, target)));
            }

            await RequirePackageIdentityAsync(payload, manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var receipt = new FileStream(Path.Combine(work, "staging.json"), FileMode.CreateNew, FileAccess.Write))
            {
                await JsonSerializer.SerializeAsync(receipt, new
                {
                    schemaVersion = 1, status = "archive_staged", installationReady = false,
                    manifestSha256 = manifest.PayloadSha256, commit = manifest.Release.Commit,
                    version = manifest.Release.Version, platform = "linux-x64", entries = entries.Count, expandedBytes = expanded
                }, cancellationToken: cancellationToken);
                await receipt.FlushAsync(cancellationToken);
                receipt.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(payload, manifest.PayloadSha256);
        }
        catch
        {
            // work was created by this call, never an existing installation.
            // Symlinks are not followed by recursive directory deletion.
            Directory.Delete(work, recursive: true);
            throw;
        }
    }

    private static string CanonicalName(string name, bool directory)
    {
        if (directory) name = name.TrimEnd('/');
        if (name.Length == 0 || name.Length > 4096 || name.StartsWith('/') || name.Contains('\\')
            || name.Any(char.IsControl) || name.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Update archive path is not a canonical relative path.");
        return name;
    }

    private static string ResolveLinkName(string name, string target)
    {
        if (string.IsNullOrEmpty(target) || target.Length > 4096 || target.StartsWith('/')
            || target.Contains('\\') || target.Any(char.IsControl))
            throw new InvalidDataException("Update archive link target is invalid.");
        var parts = name.Split('/').SkipLast(1).ToList();
        foreach (string part in target.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("Update archive link escapes its package.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return CanonicalName(string.Join('/', parts), false);
    }

    private static async Task RequirePackageIdentityAsync(string root, VerifiedUpdateManifest manifest, CancellationToken token)
    {
        string metadata = Path.Combine(root, "package.json");
        await using var file = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 64 * 1024) throw new InvalidDataException("Invalid package identity size.");
        using var json = await JsonDocument.ParseAsync(file, cancellationToken: token);
        var value = json.RootElement;
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() != 1)
            || !value.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version) || version != 1
            || !value.TryGetProperty("version", out var release) || release.GetString() != manifest.Release.Version
            || !value.TryGetProperty("commit", out var commit) || commit.GetString() != manifest.Release.Commit
            || !value.TryGetProperty("platform", out var platform) || platform.GetString() != "linux-x64")
            throw new InvalidDataException("Package identity does not match its signed update release.");
        foreach (string required in new[] { "kicad-codex", "kicad-mcp", "runtime/bin/kicad", "runtime/bin/kicad-cli",
            "runtime/lib/kicad-automation/kicad-mcp" })
        {
            string path = Path.Combine(root, required);
            if (!File.Exists(path)) throw new InvalidDataException("Missing update package entrypoint: " + required);
            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) == 0)
                throw new InvalidDataException("Update package entrypoint is not executable: " + required);
        }
    }
}
