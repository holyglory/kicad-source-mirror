using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdatePublishingTests
{
    [TestMethod]
    public async Task PublisherFilesAndSignedArtifactSurviveProvisionExportAndVerification()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Unix publisher provisioning."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-publisher-fixture-").FullName;
        try
        {
            string privatePath = Path.Combine(root, "private.pkcs8"), publicPath = Path.Combine(root, "publisher.spki");
            var key = await UpdatePublishing.CreatePublisherAsync(privatePath, publicPath, CancellationToken.None);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(privatePath));
            var exported = await UpdatePublishing.ExportPublisherAsync(privatePath, Path.Combine(root, "export.spki"), CancellationToken.None);
            Assert.AreEqual(key.PublicKeySha256, exported.PublicKeySha256);
            byte[] payload = "Synthetic signed package bytes."u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.tar.gz"), payload);
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40),
                [new("linux-x64", "tar.gz", "fixture.tar.gz", payload.Length, Convert.ToHexStringLower(SHA256.HashData(payload)))]);
            string envelopePath = Path.Combine(root, "preview.json");
            var signed = await UpdatePublishing.SignAsync(release, root, privatePath, envelopePath, null, CancellationToken.None);
            byte[] envelope = await File.ReadAllBytesAsync(envelopePath);
            var verified = UpdateManifestCodec.Verify(envelope, await File.ReadAllBytesAsync(publicPath), "preview");
            Assert.AreEqual(key.PublicKeySha256, verified.PublisherKeySha256);
            Assert.AreEqual(signed.PayloadSha256, verified.PayloadSha256);
            foreach (long sequence in new[] { 1L, 0L })
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdatePublishing.SignAsync(release with { Sequence = sequence },
                    root, privatePath, Path.Combine(root, "rejected.json"), envelopePath, CancellationToken.None));
            await UpdatePublishing.SignAsync(release with { Sequence = 2 }, root, privatePath,
                Path.Combine(root, "next.json"), envelopePath, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => UpdatePublishing.CreatePublisherAsync(privatePath, publicPath, CancellationToken.None));
            await File.AppendAllTextAsync(Path.Combine(root, "fixture.tar.gz"), "changed");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdatePublishing.SignAsync(release, root, privatePath,
                Path.Combine(root, "changed.json"), null, CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(root, "changed.json")));
            CollectionAssert.AreEqual(envelope, await File.ReadAllBytesAsync(envelopePath));
            Assert.IsEmpty(Directory.GetFiles(root, "*.partial"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationLinkedKeysBroadPermissionsAndAmbiguousDeclarationsAreRejected()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Unix publisher provisioning."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-publisher-rejection-").FullName;
        try
        {
            string privatePath = Path.Combine(root, "private.pkcs8"), publicPath = Path.Combine(root, "publisher.spki");
            await Assert.ThrowsAsync<OperationCanceledException>(() => UpdatePublishing.CreatePublisherAsync(privatePath,
                publicPath, new CancellationToken(true)));
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
            await UpdatePublishing.CreatePublisherAsync(privatePath, publicPath, CancellationToken.None);
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdatePublishing.ExportPublisherAsync(privatePath,
                Path.Combine(root, "unsafe.spki"), CancellationToken.None));
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string link = Path.Combine(root, "linked.pkcs8");
            File.CreateSymbolicLink(link, privatePath);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdatePublishing.ExportPublisherAsync(link,
                Path.Combine(root, "linked.spki"), CancellationToken.None));
            string declaration = Path.Combine(root, "release.json");
            await File.WriteAllTextAsync(declaration, "{\"schemaVersion\":1,\"schemaVersion\":2}");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdatePublishing.ReadReleaseAsync(declaration, CancellationToken.None));
            Assert.IsFalse(JsonSerializer.Serialize(new PublisherKeyReceipt(publicPath, "public-digest")).Contains("private.pkcs8", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
