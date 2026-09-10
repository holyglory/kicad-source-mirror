using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacArchivePreflightTests
{
    [TestInitialize]
    public void RequireSupportedArchiveHost()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Inconclusive("Mac archive inspection is verified on macOS and the Linux build host.");
    }
    private sealed record Item(string Path, TarEntryType Type = TarEntryType.RegularFile, byte[]? Data = null,
        string? Link = null, UnixFileMode Mode = (UnixFileMode)0x1a4, string? Attribute = null);

    [TestMethod]
    public async Task DirectoryAliasesAndCompanionRuntimeLinksRetainTheirExactMeaning()
    {
        await using var archive = await Archive(Items());
        var plan = await MacArchivePreflight.InspectAsync(archive);
        Assert.AreEqual(Items().Count, plan.Entries.Count);
        Assert.AreEqual(1, plan.ExtendedAttributeEntries);
        var link = plan.Entries.Single(x => x.Path == "install/Schematic Editor.app");
        Assert.AreEqual("install/KiCad.app/Contents/Applications/eeschema.app", link.LinkTarget);
        Assert.AreEqual("KiCad.app/Contents/Applications/eeschema.app", link.ArchivedLinkTarget);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData("Synthetic core CLR"u8)),
            plan.Entries.Single(x => x.Path == "managed/libcoreclr.dylib").Sha256);
    }

    [TestMethod]
    [DataRow("escape")]
    [DataRow("absolute")]
    [DataRow("duplicate")]
    [DataRow("case")]
    [DataRow("unicode")]
    [DataRow("cycle")]
    [DataRow("dangling")]
    [DataRow("link-write")]
    [DataRow("ancestor-link")]
    [DataRow("privileged")]
    [DataRow("fifo")]
    [DataRow("hardlink")]
    [DataRow("sparse-metadata")]
    [DataRow("missing")]
    [DataRow("not-executable")]
    public async Task UnsafeOrUnsupportedArchiveStructureIsRejectedBeforeExtraction(string fault)
    {
        var items = Items();
        switch (fault)
        {
            case "escape": items.Add(new("../outside", Data: [1])); break;
            case "absolute": items.Add(new("/absolute", Data: [1])); break;
            case "duplicate": items.Add(items[0]); break;
            case "case": items.Add(items[0] with { Path = "install/KiCad.app/Contents/INFO.PLIST" }); break;
            case "unicode": items.Add(new("install/é", Data: [1])); items.Add(new("install/e\u0301", Data: [2])); break;
            case "cycle": items.Add(new("install/a", TarEntryType.SymbolicLink, Link: "b")); items.Add(new("install/b", TarEntryType.SymbolicLink, Link: "a")); break;
            case "dangling": items.Add(new("install/a", TarEntryType.SymbolicLink, Link: "absent")); break;
            case "link-write": items.Add(new("install/Schematic Editor.app/hidden", Data: [1])); break;
            case "ancestor-link": items.Add(new("install/a", TarEntryType.SymbolicLink, Link: ".")); break;
            case "privileged": items[1] = items[1] with { Mode = (UnixFileMode)0x9ed }; break;
            case "fifo": items.Add(new("install/pipe", TarEntryType.Fifo)); break;
            case "hardlink": items.Add(new("install/hard", TarEntryType.HardLink, Link: items[1].Path)); break;
            case "sparse-metadata": items.Add(new("install/sparse", Data: [1], Attribute: "GNU.sparse.map")); break;
            case "missing": items.RemoveAt(1); break;
            case "not-executable": items[1] = items[1] with { Mode = (UnixFileMode)0x1a4 }; break;
        }
        await using var archive = await Archive(items);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacArchivePreflight.InspectAsync(archive));
    }

    [TestMethod]
    public async Task AppleDoubleEntriesAreValidatedAndAssociatedWithTheirBundledOwner()
    {
        var items = Items();
        const string owner = "install/KiCad.app/Contents/Resources/fixture";
        items.Add(new(owner, Data: "Synthetic metadata owner"u8.ToArray()));
        items.Add(new("install/KiCad.app/Contents/Resources/._fixture", Data: AppleDouble()));
        await using (var archive = await Archive(items))
        {
            var plan = await MacArchivePreflight.InspectAsync(archive);
            Assert.AreEqual(1, plan.Entries.Count(x => x.AppleDouble));
        }
        foreach (string fault in new[] { "magic", "bounds", "data-fork", "owner" })
        {
            var broken = items.ToList();
            byte[] metadata = AppleDouble();
            if (fault == "magic") metadata[0] = 0xff;
            if (fault == "bounds") BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(30), 999999);
            if (fault == "data-fork") BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(26), 1);
            if (fault == "owner") broken.RemoveAll(x => x.Path == owner);
            broken[^1] = broken[^1] with { Data = metadata };
            await using var archive = await Archive(broken);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacArchivePreflight.InspectAsync(archive));
        }
    }

    [TestMethod]
    public async Task LimitsCancellationAndRetryDoNotExtractAnyFiles()
    {
        await using var archive = await Archive(Items());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacArchivePreflight.InspectAsync(archive, 1, 100, CancellationToken.None));
        archive.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacArchivePreflight.InspectAsync(archive, 1000000, 1, CancellationToken.None));
        archive.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => MacArchivePreflight.InspectAsync(archive, new CancellationToken(true)));
        archive.Position = 0;
        Assert.IsTrue((await MacArchivePreflight.InspectAsync(archive)).ExpandedBytes > 0);
    }

    [TestMethod]
    [TestCategory("ExternalMacArchive")]
    [DataRow("KICAD_MAC_ARM64_ARCHIVE")]
    [DataRow("KICAD_MAC_X64_ARCHIVE")]
    public async Task ActualPublishedMacArchivePassesStructuralPreflight(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (path is null) { Assert.Inconclusive("Select an exact retained native Mac archive; this is not native Mac execution."); return; }
        await using var archive = File.OpenRead(path);
        var plan = await MacArchivePreflight.InspectAsync(archive);
        Assert.IsTrue(plan.Entries.Any(x => x.Type == TarEntryType.SymbolicLink));
        Assert.IsTrue(plan.Entries.Any(x => x.AppleDouble));
        Assert.IsTrue(plan.ExtendedAttributeEntries > 0);
    }

    private static List<Item> Items() =>
    [
        new("install/KiCad.app/Contents/Info.plist", Data: "Synthetic plist"u8.ToArray()),
        new("install/KiCad.app/Contents/MacOS/kicad", Data: [1], Mode: (UnixFileMode)0x1ed),
        new("install/KiCad.app/Contents/MacOS/kicad-cli", Data: [2], Mode: (UnixFileMode)0x1ed),
        new("managed/kicad-mcp", Data: [3], Mode: (UnixFileMode)0x1ed),
        new("managed/libcoreclr.dylib", Data: "Synthetic core CLR"u8.ToArray()),
        new("kicad-mcp", Data: [4], Mode: (UnixFileMode)0x1ed),
        new("install/KiCad.app/Contents/Frameworks/libnng.1.dylib", Data: [5], Attribute: "LIBARCHIVE.xattr.com.kicad.fixture"),
        new("managed/libnng.dylib", TarEntryType.SymbolicLink, Link: "../install/KiCad.app/Contents/Frameworks/libnng.1.dylib"),
        new("install/KiCad.app/Contents/Applications/eeschema.app", TarEntryType.Directory, Mode: (UnixFileMode)0x1ed),
        new("install/Schematic Editor.app", TarEntryType.SymbolicLink, Link: "KiCad.app/Contents/Applications/eeschema.app")
    ];

    private static byte[] AppleDouble()
    {
        byte[] bytes = new byte[70];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x00051607);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 0x00020000);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(26), 9);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(30), 38);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(34), 32);
        return bytes;
    }

    private static async Task<MemoryStream> Archive(IEnumerable<Item> items)
    {
        var stream = new MemoryStream();
        await using (var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
        await using (var writer = new TarWriter(gzip, leaveOpen: true))
            foreach (var item in items)
            {
                var attributes = item.Attribute is null ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [item.Attribute] = "a25vd24=" };
                var entry = new PaxTarEntry(item.Type, item.Path, attributes) { Mode = item.Mode };
                if (item.Link is not null) entry.LinkName = item.Link;
                if (item.Data is not null) entry.DataStream = new MemoryStream(item.Data);
                await writer.WriteEntryAsync(entry);
                entry.DataStream?.Dispose();
            }
        stream.Position = 0;
        return stream;
    }
}
