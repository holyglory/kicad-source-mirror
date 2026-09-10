using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsArchivePreflightTests
{
    private static readonly string[] Required = ["bin/kicad.exe", "bin/kicad-cli.exe", "bin/kicad-mcp.exe", "bin/coreclr.dll", "bin/nng.dll"];

    [TestMethod]
    public async Task ReadsNamesSizesAndHashesWithoutExtractingOrExecuting()
    {
        using var input = Create(("share/", "", 0x41ed0010), ("share/évidence.txt", "actual fixture bytes", null),
            ("share/.metadata", "", null), ("share/COM10-library.txt", "ordinary name", null));
        var plan = await WindowsArchivePreflight.InspectAsync(input);
        Assert.AreEqual(9, plan.Entries.Count);
        var file = plan.Entries.Single(e => e.Path == "share/évidence.txt");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData("actual fixture bytes"u8)), file.Sha256);
        Assert.AreEqual(20, file.Bytes); Assert.IsFalse(file.Directory);
        Assert.IsTrue(plan.Entries.Single(e => e.Path == "share").Directory);
        Assert.IsNull(plan.Entries.Single(e => e.Path == "share").Sha256);
        Assert.IsTrue(input.CanRead, "The caller owns the input stream.");
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("/root/file")]
    [DataRow("C:/root/file")]
    [DataRow("bin\\escaped.dll")]
    [DataRow("bin/a:stream")]
    [DataRow("bin//a")]
    [DataRow("bin/./a")]
    [DataRow("bin/../a")]
    [DataRow("bin/trailing.")]
    [DataRow("bin/trailing ")]
    [DataRow("bin/NUL.txt")]
    [DataRow("bin/con")]
    [DataRow("bin/LPT³.dat")]
    [DataRow("bin/COM1/a")]
    [DataRow("bin/bad?.dll")]
    [DataRow("bin/bad\u0001.dll")]
    public async Task RejectsNamesThatEscapeAliasOrAddressWindowsDevices(string name)
    {
        using var input = Create((name, "fixture", null));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input));
    }

    [TestMethod]
    [DataRow("bin/KICAD.EXE", "unused")]
    [DataRow("share/item", "share/item/nested")]
    [DataRow("share/item/nested", "share/item")]
    public async Task RejectsCaseCollisionsAndFileDirectoryConflicts(string first, string second)
    {
        using var input = Create((first, "one", null), (second, "two", null));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input));
    }

    [TestMethod]
    [DataRow(unchecked((int)0xa1ff0000))]
    [DataRow(0x10000000)]
    public async Task RejectsLinkOrSpecialUnixEntries(int attributes)
    {
        using var input = Create(("share/special", "link target", attributes));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input));
    }

    [TestMethod]
    public async Task RejectsReparsePointsAndNonemptyDirectories()
    {
        using var reparse = Create(("share/reparse", "target", (int)FileAttributes.ReparsePoint));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(reparse));
        using var directory = Create(("share/", "not empty", 0x41ed0010));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(directory));
    }

    [TestMethod]
    public async Task DetectsDataCorruptionEvenWhenZipReaderDoesNotValidateCrc()
    {
        using var original = Create(("share/corrupt.txt", "unique-payload-for-crc", null));
        byte[] bytes = original.ToArray();
        int payload = bytes.AsSpan().IndexOf("unique-payload-for-crc"u8);
        Assert.IsTrue(payload >= 0); bytes[payload] ^= 1;
        using var corrupt = new MemoryStream(bytes);
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(corrupt));
        StringAssert.Contains(failure.Message, "CRC");
    }

    [TestMethod]
    public async Task EnforcesCountsExpandedBytesAndCancellation()
    {
        using var input = Create();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input, 1000, 1, default));
        input.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input, 1, 100, default));
        input.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsArchivePreflight.InspectAsync(input, new CancellationToken(true)));
        Assert.AreEqual(0, input.Position);
    }

    [TestMethod]
    public async Task RejectsFalseDirectoryCountsBeforeMaterializingEntries()
    {
        using var original = Create(); byte[] bytes = original.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 1);
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input));
    }

    [TestMethod]
    public async Task ReadsZip64DirectoryAndStillRejectsExcessiveCounts()
    {
        using var original = Create(); byte[] narrow = original.ToArray();
        int oldEnd = narrow.Length - 22;
        byte[] wide = new byte[narrow.Length + 76];
        narrow.AsSpan(0, oldEnd).CopyTo(wide);
        var record = wide.AsSpan(oldEnd, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record[24..], 5); BinaryPrimitives.WriteUInt64LittleEndian(record[32..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(record[40..], BinaryPrimitives.ReadUInt32LittleEndian(narrow.AsSpan(oldEnd + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(record[48..], BinaryPrimitives.ReadUInt32LittleEndian(narrow.AsSpan(oldEnd + 16)));
        var locator = wide.AsSpan(oldEnd + 56, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)oldEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        narrow.AsSpan(oldEnd).CopyTo(wide.AsSpan(oldEnd + 76));
        BinaryPrimitives.WriteUInt16LittleEndian(wide.AsSpan(wide.Length - 12), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(wide.AsSpan(wide.Length - 14), ushort.MaxValue);
        using var input = new MemoryStream(wide);
        Assert.AreEqual(5, (await WindowsArchivePreflight.InspectAsync(input)).Entries.Count);
        input.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsArchivePreflight.InspectAsync(input, 10000, 1, default));
    }

    private static MemoryStream Create(params (string Name, string Contents, int? Attributes)[] extra)
    {
        var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var value in Required.Select(name => (Name: name, Contents: "synthetic non-executable fixture", Attributes: (int?)null)).Concat(extra))
            {
                var entry = zip.CreateEntry(value.Name, CompressionLevel.NoCompression);
                if (value.Attributes is int attributes) entry.ExternalAttributes = attributes;
                using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(value.Contents));
            }
        result.Position = 0; return result;
    }
}
