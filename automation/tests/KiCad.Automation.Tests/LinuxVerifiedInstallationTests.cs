using System.Security.Cryptography;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxVerifiedInstallationTests
{
    [TestMethod]
    public async Task ExistingDestinationsAndCancelledOrInvalidInputsRemainUntouched()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Verified Linux installation requires Linux."); return; }
        string scratch = Directory.CreateTempSubdirectory("kicad-install-input-").FullName;
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] envelope = UpdateManifestCodec.Sign(new(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40),
            [new("linux-x64", "tar.gz", "fixture.tar.gz", 10, new string('2', 64))]), publisher);
        string root = Path.Combine(scratch, "installation");
        string archive = Path.Combine(scratch, "missing.tar.gz");
        Uri origin = new("https://updates.example.test/");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "existing.txt"), "Existing fixture work.");
            await Assert.ThrowsAsync<IOException>(() =>
                LinuxVerifiedInstallation.InstallAsync(root, archive, envelope, publisher.ExportSubjectPublicKeyInfo(), origin, "preview"));
            Assert.AreEqual("Existing fixture work.", await File.ReadAllTextAsync(Path.Combine(root, "existing.txt")));
            string newRoot = Path.Combine(scratch, "new-installation");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxVerifiedInstallation.InstallAsync(newRoot, archive, envelope, other.ExportSubjectPublicKeyInfo(), origin, "preview"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxVerifiedInstallation.InstallAsync(newRoot, archive, envelope, publisher.ExportSubjectPublicKeyInfo(), origin, "stable"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                LinuxVerifiedInstallation.InstallAsync(newRoot, archive, envelope, publisher.ExportSubjectPublicKeyInfo(), new Uri("http://example.test/"), "preview"));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                LinuxVerifiedInstallation.InstallAsync(newRoot, archive, envelope, publisher.ExportSubjectPublicKeyInfo(), origin, "preview"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                LinuxVerifiedInstallation.InstallAsync(newRoot, archive, envelope, publisher.ExportSubjectPublicKeyInfo(), origin, "preview", cancellation.Token));
            Assert.IsFalse(Directory.Exists(newRoot));
            Assert.IsEmpty(Directory.GetDirectories(scratch, ".kicad-install-*"));
        }
        finally { Directory.Delete(scratch, true); }
    }
}
