using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxPayloadFingerprintTests
{
    [TestMethod]
    public async Task FingerprintTracksBytesNamesModesAndLinksButNotUnchangedTimestamps()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux payload fingerprint requires Linux."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-payload-fingerprint-").FullName;
        try
        {
            string file = Path.Combine(root, "payload.txt");
            await File.WriteAllTextAsync(file, "fixture");
            File.SetUnixFileMode(file, (UnixFileMode)0x1A4);
            string first = await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None);
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1));
            Assert.AreEqual(first, await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None));
            await File.WriteAllTextAsync(file, "changed");
            Assert.AreNotEqual(first, await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None));
            await File.WriteAllTextAsync(file, "fixture");
            Assert.AreEqual(first, await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None));
            File.SetUnixFileMode(file, (UnixFileMode)0x1ED);
            Assert.AreNotEqual(first, await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None));
            File.SetUnixFileMode(file, (UnixFileMode)0x1A4);
            string link = Path.Combine(root, "alias");
            File.CreateSymbolicLink(link, "payload.txt");
            string linked = await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None);
            Assert.AreNotEqual(first, linked);
            File.Delete(link);
            File.CreateSymbolicLink(link, "./payload.txt");
            Assert.AreNotEqual(linked, await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None));
            File.Delete(link);
            File.Move(file, Path.Combine(root, "renamed.txt"));
            Assert.AreNotEqual(first, await LinuxPayloadFingerprint.ComputeAsync(root, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
