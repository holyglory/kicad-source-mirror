using System.Formats.Tar;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KiCad.Automation.Distribution;

public sealed record MacArchiveEntry(string Path, TarEntryType Type, long Bytes, UnixFileMode Mode,
    string? Sha256, string? LinkTarget, bool AppleDouble = false, string? ArchivedLinkTarget = null);
public sealed record MacArchivePlan(IReadOnlyList<MacArchiveEntry> Entries, long ExpandedBytes,
    int ExtendedAttributeEntries);

/// <summary>Inspect archive structure and data without extracting or executing it.</summary>
public static class MacArchivePreflight
{
    public static Task<MacArchivePlan> InspectAsync(Stream compressed, CancellationToken token = default) =>
        InspectAsync(compressed, 64L * 1024 * 1024 * 1024, 200_000, token);

    internal static Task<MacArchivePlan> InspectAsync(Stream compressed, long maximumBytes,
        int maximumEntries, CancellationToken token) => Task.Run(() => Inspect(compressed, maximumBytes, maximumEntries, token), token);

    private static MacArchivePlan Inspect(Stream compressed, long maximumBytes, int maximumEntries, CancellationToken token)
    {
        if (maximumBytes < 1 || maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var entries = new Dictionary<string, MacArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        int attributeEntries = 0;
        using var reader = new MacNativeArchiveReader(compressed, token);
        byte[] buffer = new byte[128 * 1024];
        while (reader.Next() is { } entry)
        {
            token.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile
                or TarEntryType.V7RegularFile or TarEntryType.SymbolicLink))
                throw new InvalidDataException("Unsupported Mac update archive entry: " + entry.EntryType);
            string name = Canonical(entry.Name, entry.EntryType == TarEntryType.Directory);
            if (entries.Count >= maximumEntries || entries.ContainsKey(Key(name)))
                throw new InvalidDataException("Duplicate, case-colliding or excessive Mac archive entries.");
            if ((entry.Mode & ~(UnixFileMode)0x1ff) != 0)
                throw new InvalidDataException("Mac update entries cannot carry privileged permission bits.");
            if (entry.ExtendedAttributeCount > 0) attributeEntries++;
            if (entry.Length < 0 || entry.Length > maximumBytes - expanded)
                throw new InvalidDataException("Mac archive expanded size exceeds its limit.");
            expanded += entry.Length;
            string? hash = null, link = null;
            bool appleDouble = name.Split('/')[^1].StartsWith("._", StringComparison.Ordinal);
            if (appleDouble && (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.Length > 16 * 1024 * 1024))
                throw new InvalidDataException("Invalid or excessive AppleDouble metadata entry.");
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var metadata = appleDouble ? new MemoryStream() : null;
                long read = 0;
                int count;
                while ((count = reader.ReadData(buffer)) != 0)
                {
                    read += count;
                    if (read > entry.Length) throw new InvalidDataException("Archive entry exceeds its declared length.");
                    digest.AppendData(buffer, 0, count);
                    metadata?.Write(buffer, 0, count);
                }
                if (read != entry.Length) throw new InvalidDataException("Truncated Mac archive entry.");
                hash = Convert.ToHexStringLower(digest.GetHashAndReset());
                if (metadata is not null) ValidateAppleDouble(metadata.ToArray());
            }
            else if (entry.Length != 0) throw new InvalidDataException("Non-file Mac archive entry contains data.");
            if (entry.EntryType == TarEntryType.SymbolicLink) link = ResolveRelative(name, entry.LinkName);
            entries.Add(Key(name), new(name, entry.EntryType, entry.Length, entry.Mode, hash, link,
                appleDouble, entry.EntryType == TarEntryType.SymbolicLink ? entry.LinkName : null));
        }
        if (entries.Count == 0) throw new InvalidDataException("The Mac update archive is empty.");
        ValidateTree(entries);
        RequireLayout(entries);
        return new(Array.AsReadOnly(entries.Values.ToArray()), expanded, attributeEntries);
    }

    private static void ValidateTree(Dictionary<string, MacArchiveEntry> entries)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            if (entry.AppleDouble)
            {
                string target = AppleDoubleTarget(entry.Path);
                if (!entries.TryGetValue(Key(target), out var owner) || owner.AppleDouble
                    || owner.Type is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("AppleDouble metadata lacks an ordinary bundled owner.");
            }
            if (entry.Type == TarEntryType.Directory) directories.Add(Key(entry.Path));
            string[] parts = entry.Path.Split('/');
            for (int count = 1; count < parts.Length; count++)
            {
                string parent = Key(string.Join('/', parts.Take(count)));
                if (entries.TryGetValue(parent, out var ancestor) && ancestor.Type != TarEntryType.Directory)
                    throw new InvalidDataException("An archive entry writes through a file or symbolic link.");
                directories.Add(parent);
            }
        }
        foreach (var link in entries.Values.Where(x => x.Type == TarEntryType.SymbolicLink))
        {
            string target = link.LinkTarget!;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(link.Path) };
            while (true)
            {
                string[] parts = target.Split('/');
                bool followed = false;
                for (int count = 1; count <= parts.Length; count++)
                {
                    string prefix = Key(string.Join('/', parts.Take(count)));
                    if (!entries.TryGetValue(prefix, out var next) || next.Type != TarEntryType.SymbolicLink) continue;
                    if (!visited.Add(prefix) || visited.Count > 256)
                        throw new InvalidDataException("Cyclic or excessively deep Mac archive link.");
                    target = next.LinkTarget! + (count < parts.Length ? "/" + string.Join('/', parts.Skip(count)) : "");
                    followed = true;
                    break;
                }
                if (!followed) break;
            }
            string key = Key(target);
            bool directory = directories.Contains(key);
            if (!directory && (!entries.TryGetValue(key, out var terminal)
                || terminal.Type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)))
                throw new InvalidDataException("A Mac archive link is dangling or resolves to an unsupported entry.");
            if (directory && Key(link.Path).StartsWith(key + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A directory alias loops back into its own ancestor.");
        }
    }

    private static void RequireLayout(Dictionary<string, MacArchiveEntry> entries)
    {
        foreach (string name in new[] { "install/KiCad.app/Contents/Info.plist", "install/KiCad.app/Contents/MacOS/kicad",
            "install/KiCad.app/Contents/MacOS/kicad-cli", "managed/kicad-mcp", "managed/libcoreclr.dylib", "kicad-mcp" })
        {
            if (!entries.TryGetValue(Key(name), out var entry)
                || entry.Type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Missing ordinary Mac package entry: " + name);
            if (!name.EndsWith("Info.plist", StringComparison.Ordinal) && name != "managed/libcoreclr.dylib"
                && (entry.Mode & UnixFileMode.UserExecute) == 0)
                throw new InvalidDataException("Mac package entry is not executable: " + name);
        }
        if (!entries.TryGetValue(Key("managed/libnng.dylib"), out var nng)
            || nng.Type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.SymbolicLink))
            throw new InvalidDataException("Missing bundled Mac NNG binding.");
    }

    internal static string Canonical(string name, bool directory)
    {
        if (directory) name = name.TrimEnd('/');
        if (name.Length is < 1 or > 4096 || name.StartsWith('/') || name.Contains('\\') || name.Any(char.IsControl)
            || name.Split('/').Any(x => x is "" or "." or "..")
            || name.Split('/')[0] is not ("install" or "managed" or "kicad-mcp"))
            throw new InvalidDataException("Invalid or out-of-layout Mac archive path.");
        return name;
    }

    private static string ResolveRelative(string name, string target)
    {
        if (string.IsNullOrEmpty(target) || target.Length > 4096 || target.StartsWith('/')
            || target.Contains('\\') || target.Any(char.IsControl)) throw new InvalidDataException("Invalid Mac archive link target.");
        var parts = name.Split('/').SkipLast(1).ToList();
        foreach (string part in target.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("Mac archive link escapes its package.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return Canonical(string.Join('/', parts), false);
    }

    private static string Key(string name) => name.Normalize(NormalizationForm.FormC);
    internal static string AppleDoubleTarget(string path)
    {
        int last = path.LastIndexOf('/') + 1;
        return Canonical(path[..last] + path[(last + 2)..], false);
    }

    private static void ValidateAppleDouble(byte[] bytes)
    {
        if (bytes.Length < 26 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x00051607
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)) != 0x00020000)
            throw new InvalidDataException("Malformed AppleDouble metadata header.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(24));
        int header = 26 + 12 * count;
        if (count is < 1 or > 64 || header > bytes.Length) throw new InvalidDataException("Malformed AppleDouble entry table.");
        var identifiers = new HashSet<uint>();
        var ranges = new List<(ulong Start, ulong End)>();
        for (int index = 0; index < count; index++)
        {
            var descriptor = bytes.AsSpan(26 + 12 * index, 12);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(descriptor);
            ulong offset = BinaryPrimitives.ReadUInt32BigEndian(descriptor[4..]);
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(descriptor[8..]);
            if (id == 1 || !identifiers.Add(id) || offset < (ulong)header || offset + length > (ulong)bytes.Length
                || ranges.Any(x => length != 0 && offset < x.End && offset + length > x.Start))
                throw new InvalidDataException("Invalid AppleDouble data-fork, overlap or entry bounds.");
            ranges.Add((offset, offset + length));
        }
    }
}
