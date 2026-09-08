using System.ComponentModel;
using System.Security.Cryptography;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxUpdateRuntimeTests
{
    [TestMethod]
    public async Task FailedWrongAndExcessiveNativeOutputAreRejectedWithoutChangingCandidate()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("This is the Linux native-runtime validator."); return; }
        // These compiled OS utilities are deliberately invalid native-CLI test
        // doubles. The success case uses real packaged KiCad in update-archive.
        foreach (string executable in new[] { "/usr/bin/true", "/usr/bin/false", "/usr/bin/yes" })
        {
            using var fixture = new RuntimeFixture();
            File.Copy(executable, fixture.Executable);
            File.SetUnixFileMode(fixture.Executable, (UnixFileMode)0x1ED);
            byte[] original = await File.ReadAllBytesAsync(fixture.Executable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxUpdateRuntime.CheckAsync(fixture.Manifest, fixture.Staged, fixture.Scratch, deadline.Token));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Executable));
            Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Scratch));
        }
    }

    [TestMethod]
    public async Task UnlaunchableAndCancelledChecksDoNotLeaveScratchOrRemoveCandidate()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("This is the Linux native-runtime validator."); return; }
        using var fixture = new RuntimeFixture();
        await File.WriteAllTextAsync(fixture.Executable, "Synthetic file that is not executable.");
        File.SetUnixFileMode(fixture.Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.ThrowsExactlyAsync<Win32Exception>(() =>
            LinuxUpdateRuntime.CheckAsync(fixture.Manifest, fixture.Staged, fixture.Scratch));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Scratch));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LinuxUpdateRuntime.CheckAsync(fixture.Manifest, fixture.Staged, fixture.Scratch, cancelled.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Scratch));
        Assert.IsTrue(File.Exists(fixture.Executable));
        var wrong = new StagedLinuxUpdate(fixture.Staged.Directory, new string('7', 64));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateRuntime.CheckAsync(fixture.Manifest, wrong, fixture.Scratch));
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("kicad-runtime-check-").FullName;
        public string Scratch { get; }
        public string Executable { get; }
        public VerifiedUpdateManifest Manifest { get; }
        public StagedLinuxUpdate Staged { get; }
        public RuntimeFixture()
        {
            Scratch = Directory.CreateDirectory(Path.Combine(root, "scratch")).FullName;
            string candidate = Directory.CreateDirectory(Path.Combine(root, "candidate")).FullName;
            Directory.CreateDirectory(Path.Combine(candidate, "runtime/bin"));
            Executable = Path.Combine(candidate, "runtime/bin/kicad-cli");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40),
                [new("linux-x64", "tar.gz", "fixture.tar.gz", 10, new string('2', 64))]);
            Manifest = UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, key), key.ExportSubjectPublicKeyInfo(), "preview");
            Staged = new(candidate, Manifest.PayloadSha256);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
