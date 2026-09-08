using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxUpdateStagerTests
{
    [TestMethod]
    public async Task StagesNewTreePreservingModesAndInternalLibraryLinks()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux archive modes require Linux."); return; }
        using var fixture = new ArchiveFixture();
        var download = await fixture.CreateAsync([
            .. Entries(), new("runtime/lib/library.so", Link: "library.so.1"),
            new("runtime/lib/library.so.1", Link: "library.so.1.0"), new("runtime/lib/library.so.1.0", Data: "Synthetic shared library.")
        ]);
        string prior = Path.Combine(fixture.Root, "existing-installation");
        await File.WriteAllTextAsync(prior, "Existing work must remain.");
        var staged = await LinuxUpdateStager.StageAsync(fixture.Manifest!, download, fixture.Root);
        Assert.AreEqual("Existing work must remain.", await File.ReadAllTextAsync(prior));
        Assert.AreEqual("Synthetic shared library.", await File.ReadAllTextAsync(Path.Combine(staged.Directory, "runtime/lib/library.so")));
        Assert.AreEqual("library.so.1", new FileInfo(Path.Combine(staged.Directory, "runtime/lib/library.so")).LinkTarget);
        Assert.IsTrue((File.GetUnixFileMode(Path.Combine(staged.Directory, "kicad-codex")) & UnixFileMode.UserExecute) != 0);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(staged.Directory)!, "staging.json")));
        Assert.IsFalse(receipt.RootElement.GetProperty("installationReady").GetBoolean());
        Assert.AreEqual(fixture.Manifest!.PayloadSha256, staged.ManifestSha256);
    }

    [TestMethod]
    public async Task RejectsEscapingDuplicateSpecialAndBrokenLinkEntries()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux archive modes require Linux."); return; }
        ArchiveItem[][] faults = [
            [new("../outside", Data: "escape")], [new("/absolute", Data: "escape")],
            [new("runtime/../outside", Data: "noncanonical")],
            [new("runtime/a", Data: "first"), new("runtime/a", Data: "second")],
            [new("runtime/link", Link: "../../outside")], [new("runtime/link", Link: "/etc/passwd")],
            [new("runtime/link", Link: "missing")],
            [new("runtime/a", Link: "b"), new("runtime/b", Link: "a")],
            [new("runtime/link", Link: "../runtime"), new("runtime/link/child", Data: "no link traversal")],
            [new("runtime/pipe", Type: TarEntryType.Fifo)],
            [new("runtime/hard", Link: "kicad-codex", Type: TarEntryType.HardLink)]
        ];
        foreach (var fault in faults)
        {
            using var fixture = new ArchiveFixture();
            var download = await fixture.CreateAsync([.. Entries(), .. fault]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LinuxUpdateStager.StageAsync(fixture.Manifest!, download, fixture.Root));
            Assert.IsEmpty(Directory.GetDirectories(fixture.Root, "stage-*"));
            Assert.IsTrue(File.Exists(download.Path));
        }
    }

    [TestMethod]
    public async Task WrongIdentityMissingEntryPointsAndChangedDownloadAreRejected()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux archive modes require Linux."); return; }
        foreach (string fault in new[] { "commit", "missing", "mode", "changed", "manifest" })
        {
            using var fixture = new ArchiveFixture();
            var entries = Entries(fault == "commit" ? new string('3', 40) : null);
            if (fault == "missing") entries.RemoveAll(entry => entry.Name == "kicad-codex");
            if (fault == "mode") entries[1] = entries[1] with { Executable = false };
            var download = await fixture.CreateAsync(entries);
            if (fault == "changed") await File.AppendAllTextAsync(download.Path, "unexpected bytes");
            var manifest = fixture.Manifest!;
            if (fault == "manifest") manifest = new(manifest.Release with { Version = "another" }, new string('5', 64), manifest.PublisherKeySha256);
            await Assert.ThrowsAsync<InvalidDataException>(() => LinuxUpdateStager.StageAsync(manifest, download, fixture.Root));
            Assert.IsEmpty(Directory.GetDirectories(fixture.Root, "stage-*"));
        }
    }

    [TestMethod]
    public async Task ExpansionLimitsAndCancellationLeaveNoPartialInstallation()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux archive modes require Linux."); return; }
        using var fixture = new ArchiveFixture();
        var download = await fixture.CreateAsync(Entries());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateStager.StageAsync(fixture.Manifest!, download, fixture.Root, 10, 100, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateStager.StageAsync(fixture.Manifest!, download, fixture.Root, 100_000, 1, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LinuxUpdateStager.StageAsync(fixture.Manifest!, download, fixture.Root, cancellation.Token));
        Assert.IsEmpty(Directory.GetDirectories(fixture.Root, "stage-*"));
        Assert.IsTrue(File.Exists(download.Path));
        Assert.IsTrue(Directory.Exists((await LinuxUpdateStager.StageAsync(fixture.Manifest!, download, fixture.Root)).Directory));
    }

    private static List<ArchiveItem> Entries(string? commit = null) => [
        new("package.json", JsonSerializer.Serialize(new
        { schemaVersion = 1, version = "fixture", commit = commit ?? new string('1', 40), platform = "linux-x64" })),
        new("kicad-codex", "Synthetic launcher", Executable: true),
        new("kicad-mcp", "Synthetic launcher", Executable: true),
        new("runtime/bin/kicad", "Synthetic manager", Executable: true),
        new("runtime/bin/kicad-cli", "Synthetic CLI", Executable: true),
        new("runtime/lib/kicad-automation/kicad-mcp", "Synthetic MCP", Executable: true)
    ];

    private sealed record ArchiveItem(string Name, string? Data = null, string? Link = null,
        TarEntryType Type = TarEntryType.RegularFile, bool Executable = false);

    private sealed class ArchiveFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kicad-update-stage-").FullName;
        public VerifiedUpdateManifest? Manifest { get; private set; }
        public async Task<DownloadedUpdate> CreateAsync(IEnumerable<ArchiveItem> entries)
        {
            string path = Path.Combine(Root, "fixture.tar.gz");
            await using (var file = File.Create(path))
            await using (var gzip = new GZipStream(file, CompressionMode.Compress))
            await using (var tar = new TarWriter(gzip, leaveOpen: true))
            {
                foreach (var item in entries)
                {
                    var entry = new PaxTarEntry(item.Link is not null && item.Type == TarEntryType.RegularFile
                        ? TarEntryType.SymbolicLink : item.Type, item.Name)
                    { Mode = item.Executable ? (UnixFileMode)0x1ED : (UnixFileMode)0x1A4 };
                    if (item.Link is not null) entry.LinkName = item.Link;
                    if (item.Data is not null) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(item.Data));
                    await tar.WriteEntryAsync(entry);
                    entry.DataStream?.Dispose();
                }
            }
            byte[] bytes = await File.ReadAllBytesAsync(path);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var artifact = new UpdateArtifact("linux-x64", "tar.gz", "fixture.tar.gz", bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40), [artifact]);
            Manifest = UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, key), key.ExportSubjectPublicKeyInfo(), "preview");
            return new(path, artifact, Manifest.PayloadSha256);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
