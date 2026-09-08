using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateManifestTests
{
    [TestMethod]
    public void SignatureAndExactInstallationIdentitySurviveRoundTrip()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = Fixture();
        byte[] envelope = UpdateManifestCodec.Sign(release, key);
        var verified = UpdateManifestCodec.Verify(envelope, key.ExportSubjectPublicKeyInfo(), "preview");
        Assert.AreEqual(release.Commit, verified.Release.Commit);
        Assert.AreEqual("linux.tar.gz", verified.ForInstallation("linux-x64", "tar.gz")!.FileName);
        Assert.AreEqual("linux.deb", verified.ForInstallation("linux-x64", "deb")!.FileName);
        Assert.AreEqual("arm.zip", verified.ForInstallation("osx-arm64", "zip")!.FileName);
        Assert.IsNull(verified.ForInstallation("osx-x64", "zip"));
        Assert.ThrowsExactly<InvalidDataException>(() => verified.ForInstallation("osx-arm64", "deb"));
        Assert.ThrowsExactly<InvalidDataException>(() => verified.ForInstallation("linux-arm64", "tar.gz"));
        var checkpoint = new AcceptedUpdateManifest(verified.Release.Sequence, verified.PayloadSha256);
        Assert.AreEqual(verified.PayloadSha256,
            UpdateManifestCodec.Verify(envelope, key.ExportSubjectPublicKeyInfo(), "preview", checkpoint).PayloadSha256);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<UpdateArtifact>)verified.Release.Artifacts).Add(release.Artifacts[0]));
    }

    [TestMethod]
    public void WrongPublisherTamperingAndKeySubstitutionAreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signed = UpdateManifestCodec.Sign(Fixture(), key);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Verify(signed, wrong.ExportSubjectPublicKeyInfo(), "preview"));
        using var parsed = JsonDocument.Parse(signed);
        byte[] payload = Convert.FromBase64String(parsed.RootElement.GetProperty("payload").GetString()!);
        byte[] changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("preview-1", "preview-2"));
        byte[] tampered = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, payload = Convert.ToBase64String(changed),
            signature = parsed.RootElement.GetProperty("signature").GetString()
        });
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Verify(tampered, key.ExportSubjectPublicKeyInfo(), "preview"));
        byte[] keyWithExtra = [.. key.ExportSubjectPublicKeyInfo(), 0];
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Verify(signed, keyWithExtra, "preview"));
        using var unsupported = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateManifestCodec.Sign(Fixture(), unsupported));
    }

    [TestMethod]
    public void ReplayAndConflictingSequenceDoNotReplaceAnAcceptedManifest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        var current = UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(Fixture() with { Sequence = 2 }, key), spki, "preview");
        var checkpoint = new AcceptedUpdateManifest(2, current.PayloadSha256);
        foreach (var release in new[] { Fixture(), Fixture() with { Sequence = 2, Version = "preview-changed" } })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, key), spki, "preview", checkpoint));
        Assert.AreEqual(3L, UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(Fixture() with { Sequence = 3 }, key),
            spki, "preview", checkpoint).Release.Sequence);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(Fixture(), key), spki, "stable"));
    }

    [TestMethod]
    public void AuthenticatedButInvalidInstructionsAreStillRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = Fixture();
        foreach (var invalid in new[]
        {
            valid with { SchemaVersion = 2 }, valid with { Product = "other" },
            valid with { Sequence = 0 }, valid with { Commit = "main" }, valid with { Version = "../release" },
            valid with { Channel = "unknown" }, valid with { Artifacts = [] },
            valid with { Artifacts = [valid.Artifacts[0], valid.Artifacts[0]] },
            valid with { Artifacts = [valid.Artifacts[0] with { FileName = null! }] },
            valid with { Artifacts = [valid.Artifacts[0] with { FileName = "../linux.tar.gz" }] },
            valid with { Artifacts = [valid.Artifacts[0] with { Bytes = 0 }] },
            valid with { Artifacts = [valid.Artifacts[0] with { Bytes = UpdateManifestCodec.MaximumArtifactBytes + 1 }] },
            valid with { Artifacts = [valid.Artifacts[0] with { Sha256 = "unknown" }] },
            valid with { Artifacts = [valid.Artifacts[0] with { Platform = "osx-x64" }] }
        })
        {
            string json = JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdateManifestCodec.Verify(RawSigned(json, key), key.ExportSubjectPublicKeyInfo(), "preview"));
        }
        string validJson = JsonSerializer.Serialize(valid, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (string json in new[]
        {
            validJson.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":2"),
            validJson.Replace("\"sequence\":1", "\"sequence\":1,\"Sequence\":2"),
            validJson.Replace("\"schemaVersion\":1", "\"SchemaVersion\":1"),
            validJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unrecognized\":true")
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdateManifestCodec.Verify(RawSigned(json, key), key.ExportSubjectPublicKeyInfo(), "preview"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Verify(new byte[UpdateManifestCodec.MaximumEnvelopeBytes + 1], key.ExportSubjectPublicKeyInfo(), "preview"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Verify("{\"schemaVersion\":1,\"schemaVersion\":1}"u8.ToArray(), key.ExportSubjectPublicKeyInfo(), "preview"));
    }

    private static UpdateRelease Fixture() => new(1, "kicad-codex", "preview", 1, "preview-1",
        new string('1', 40), [
            new("linux-x64", "tar.gz", "linux.tar.gz", 10, new string('2', 64)),
            new("linux-x64", "deb", "linux.deb", 11, new string('3', 64)),
            new("osx-arm64", "zip", "arm.zip", 12, new string('4', 64))
        ]);

    private static byte[] RawSigned(string json, ECDsa key)
    {
        // Synthetic publisher test key, never a production signing identity.
        byte[] payload = Encoding.UTF8.GetBytes(json);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
    }
}
