using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace KiCad.Automation.Distribution;

public sealed record WindowsArchiveEntry(string Path, bool Directory, long Bytes, string? Sha256);
public sealed record WindowsArchivePlan(IReadOnlyList<WindowsArchiveEntry> Entries, long ExpandedBytes);

/// <summary>Validate the complete Windows ZIP before creating extracted files. Does not install or execute it.</summary>
public static class WindowsArchivePreflight
{
    private static readonly uint[] CrcTable = MakeCrcTable();
    private static readonly string[] Required = ["bin/kicad.exe", "bin/kicad-cli.exe", "bin/kicad-mcp.exe", "bin/coreclr.dll", "bin/nng.dll"];

    public static Task<WindowsArchivePlan> InspectAsync(Stream input, CancellationToken token = default) =>
        InspectAsync(input, 64L * 1024 * 1024 * 1024, 200_000, token);

    internal static Task<WindowsArchivePlan> InspectAsync(Stream input, long maximumBytes, int maximumEntries, CancellationToken token) =>
        Task.Run(() => Inspect(input, maximumBytes, maximumEntries, token), token);

    private static WindowsArchivePlan Inspect(Stream input, long maximumBytes, int maximumEntries, CancellationToken token)
    {
        if (maximumBytes < 1 || maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("Windows ZIP inspection requires a readable seekable stream.");
        token.ThrowIfCancellationRequested();
        BoundDirectory(input, maximumEntries);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > maximumEntries) throw new InvalidDataException("Excessive Windows ZIP entry count.");
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<WindowsArchiveEntry>();
        long expanded = 0;
        byte[] buffer = new byte[128 * 1024];
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            bool directory = entry.FullName.EndsWith('/');
            string name = Canonical(entry.FullName, directory);
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint unixType = (attributes >> 16) & 0xf000;
            if ((attributes & (uint)FileAttributes.ReparsePoint) != 0 || unixType is not (0 or 0x4000 or 0x8000)
                || (unixType == 0x4000 && !directory) || (unixType == 0x8000 && directory)
                || ((attributes & (uint)FileAttributes.Directory) != 0 && !directory))
                throw new InvalidDataException("Links, reparse points and special Windows archive entries are unsupported: " + name);
            if (!declared.Add(name) || (names.TryGetValue(name, out bool existingDirectory) && (!existingDirectory || !directory)))
                throw new InvalidDataException("Duplicate or file/directory-colliding Windows archive name: " + name);
            names[name] = directory;
            string parent = name;
            while (parent.LastIndexOf('/') is int slash && slash >= 0)
            {
                parent = parent[..slash];
                if (names.TryGetValue(parent, out bool isDirectory) && !isDirectory)
                    throw new InvalidDataException("A Windows archive file is used as a directory: " + parent);
                names[parent] = true;
            }
            if (entry.Length < 0 || entry.Length > maximumBytes - expanded || (directory && entry.Length != 0))
                throw new InvalidDataException("Invalid or excessive expanded Windows archive data.");
            expanded += entry.Length;
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            uint crc = 0xffffffff;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (count > entry.Length - read) throw new InvalidDataException("Windows ZIP data exceeds its declared length.");
                hash.AppendData(buffer, 0, count);
                for (int index = 0; index < count; index++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[index]) & 255];
                read += count;
            }
            // .NET 10 exposes the recorded CRC but does not validate it while reading.
            if (read != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException("Windows ZIP data length or CRC mismatch: " + name);
            plan.Add(new(name, directory, read, directory ? null : Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        foreach (string required in Required)
            if (!plan.Any(entry => entry.Path == required && !entry.Directory && entry.Bytes > 0))
                throw new InvalidDataException("The Windows archive lacks its canonical installed entry point: " + required);
        return new(Array.AsReadOnly(plan.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray()), expanded);
    }

    internal static string Canonical(string name, bool directory)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 32000 || name.StartsWith('/') || name.Contains('\\') || name.Contains('\ufffd'))
            throw new InvalidDataException("Invalid Windows ZIP path.");
        if (directory) name = name[..^1];
        foreach (string part in name.Split('/'))
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                throw new InvalidDataException("Invalid Windows archive path component: " + part);
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("Windows device names cannot be update files: " + part);
        }
        return name;
    }

    // Bound central-directory allocation before ZipArchive materializes its entries.
    private static void BoundDirectory(Stream input, int maximumEntries)
    {
        if (input.Length < 22 || input.Length > UpdateManifestCodec.MaximumArtifactBytes)
            throw new InvalidDataException("Invalid Windows ZIP size.");
        long original = input.Position;
        try
        {
            byte[] tail = new byte[(int)Math.Min(input.Length, 65557)];
            input.Position = input.Length - tail.Length; input.ReadExactly(tail);
            int end = -1;
            for (int index = tail.Length - 22; index >= 0; index--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50
                    && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length)
                { end = index; break; }
            if (end < 0) throw new InvalidDataException("Missing Windows ZIP end record.");
            ReadOnlySpan<byte> record = tail.AsSpan(end);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != BinaryPrimitives.ReadUInt16LittleEndian(record[10..]))
                throw new InvalidDataException("Split Windows ZIP archives are unsupported.");
            ulong entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            ulong bytes = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            long directoryEnd = input.Length - tail.Length + end;
            if (entries == ushort.MaxValue || bytes == uint.MaxValue || offset == uint.MaxValue)
            {
                if (directoryEnd < 20) throw new InvalidDataException("Missing ZIP64 locator.");
                byte[] locator = new byte[20]; input.Position = directoryEnd - 20; input.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                    throw new InvalidDataException("Invalid or split ZIP64 locator.");
                ulong position = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                if (position > (ulong)(directoryEnd - 20) || (ulong)(directoryEnd - 20) - position < 56)
                    throw new InvalidDataException("Invalid ZIP64 end position.");
                byte[] wide = new byte[56]; input.Position = (long)position; input.ReadExactly(wide);
                if (BinaryPrimitives.ReadUInt32LittleEndian(wide) != 0x06064b50
                    || BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(4)) != (ulong)(directoryEnd - 20) - position - 12
                    || BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(16)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(wide.AsSpan(20)) != 0
                    || BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(24)) != BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(32)))
                    throw new InvalidDataException("Invalid ZIP64 end record.");
                entries = BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(32));
                bytes = BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(40));
                offset = BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(48));
                directoryEnd = (long)position;
            }
            if (entries > (ulong)maximumEntries || bytes > 64 * 1024 * 1024
                || offset > (ulong)directoryEnd || bytes > (ulong)directoryEnd - offset)
                throw new InvalidDataException("Excessive or invalid Windows ZIP directory.");
            input.Position = (long)offset;
            long centralEnd = (long)(offset + bytes);
            byte[] header = new byte[46];
            ulong actualEntries = 0;
            while (input.Position < centralEnd)
            {
                if (++actualEntries > (ulong)maximumEntries || centralEnd - input.Position < header.Length)
                    throw new InvalidDataException("Excessive or truncated Windows ZIP directory entries.");
                input.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                    throw new InvalidDataException("Invalid Windows ZIP directory entry.");
                int extra = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
                if (extra > centralEnd - input.Position) throw new InvalidDataException("Truncated Windows ZIP directory metadata.");
                input.Position += extra;
            }
            if (actualEntries != entries) throw new InvalidDataException("Windows ZIP directory entry count mismatch.");
        }
        finally { input.Position = original; }
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0U : 0xedb88320U);
            table[index] = value;
        }
        return table;
    }
}
