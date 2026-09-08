using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SignedUpdateServerTests
{
    [TestMethod]
    public async Task EmptyChannelSetStillRequiresARealPublicKey()
    {
        using var fixture = await Fixture.CreateAsync();
        File.Delete(fixture.EnvelopePath);
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        Assert.IsFalse((await SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue)).TryGet("preview", out _));
        await File.WriteAllTextAsync(fixture.PublicKeyPath, "not a public key");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue));
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await File.WriteAllBytesAsync(fixture.PublicKeyPath, wrongCurve.ExportSubjectPublicKeyInfo());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue));
    }

    [TestMethod]
    public async Task HttpServesImmutableSignedMetadataWithoutSigningOrPrivateAccess()
    {
        using var fixture = await Fixture.CreateAsync();
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        var updates = await SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue);
        Assert.IsTrue(updates.TryGet("preview", out var feed));
        byte[] original = feed!.CopyEnvelope();
        byte[] changed = feed.CopyEnvelope();
        changed[0] ^= 1;
        CollectionAssert.AreEqual(original, feed.CopyEnvelope());
        await using var app = DownloadServer.Create(catalogue, "http://127.0.0.1:0", updates);
        await app.StartAsync();
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.GetAsync("/updates/preview.json");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("\"" + feed.Sha256 + "\"", response.Headers.ETag?.Tag);
            byte[] downloaded = await response.Content.ReadAsByteArrayAsync();
            CollectionAssert.AreEqual(original, downloaded);
            Assert.AreEqual(fixture.Release.Commit, UpdateManifestCodec.Verify(downloaded, fixture.Key.ExportSubjectPublicKeyInfo(), "preview").Release.Commit);
            using var head = await http.SendAsync(new(HttpMethod.Head, "/updates/preview.json"));
            Assert.AreEqual(original.Length, head.Content.Headers.ContentLength);
            Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync());
            foreach (string path in new[] { "/updates/stable.json", "/updates/private.json", "/publisher.spki", "/private.pkcs8", "/sign", "/mcp" })
                Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode, path);
            using var write = await http.PostAsync("/updates/preview.json", new StringContent("not an upload API"));
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, write.StatusCode);
            await File.WriteAllTextAsync(fixture.EnvelopePath, "changed on disk");
            CollectionAssert.AreEqual(original, await http.GetByteArrayAsync("/updates/preview.json"),
                "The running generation serves its verified in-memory envelope, not mutable file bytes.");
            await File.AppendAllTextAsync(fixture.ArtifactPath, "changed");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/updates/preview.json")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task WrongPublisherMismatchedPackagesUnsupportedChannelsAndLinksCannotLoad()
    {
        using var fixture = await Fixture.CreateAsync();
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllBytesAsync(fixture.PublicKeyPath, other.ExportSubjectPublicKeyInfo());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue));
        await File.WriteAllBytesAsync(fixture.PublicKeyPath, fixture.Key.ExportSubjectPublicKeyInfo());
        foreach (var invalid in new[]
        {
            fixture.Release with { Commit = new string('3', 40) },
            fixture.Release with { Version = "another-version" },
            fixture.Release with { Channel = "stable" },
            fixture.Release with { Artifacts = [fixture.Release.Artifacts[0] with { FileName = "unlisted.tar.gz" }] },
            fixture.Release with { Artifacts = [fixture.Release.Artifacts[0] with { Sha256 = new string('4', 64) }] }
        })
        {
            await File.WriteAllBytesAsync(fixture.EnvelopePath, UpdateManifestCodec.Sign(invalid, fixture.Key));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue));
        }
        await File.WriteAllBytesAsync(fixture.EnvelopePath, UpdateManifestCodec.Sign(fixture.Release, fixture.Key));
        Assert.IsTrue((await SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue)).TryGet("preview", out _));
        if (!OperatingSystem.IsWindows())
        {
            File.Move(fixture.EnvelopePath, fixture.EnvelopePath + ".target");
            File.CreateSymbolicLink(fixture.EnvelopePath, fixture.EnvelopePath + ".target");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.PublicKeyPath, catalogue));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kicad-signed-site-").FullName;
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKeyPath => Path.Combine(Root, "publisher.spki");
        public string ArtifactPath => Path.Combine(Root, "fixture.tar.gz");
        public string EnvelopePath => Path.Combine(Root, "updates/preview.json");
        public UpdateRelease Release { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            byte[] bytes = "Synthetic signed site package."u8.ToArray();
            await File.WriteAllBytesAsync(fixture.ArtifactPath, bytes);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            fixture.Release = new(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40),
                [new("linux-x64", "tar.gz", "fixture.tar.gz", bytes.Length, hash)]);
            await File.WriteAllBytesAsync(fixture.PublicKeyPath, fixture.Key.ExportSubjectPublicKeyInfo());
            Directory.CreateDirectory(Path.Combine(fixture.Root, "updates"));
            await File.WriteAllBytesAsync(fixture.EnvelopePath, UpdateManifestCodec.Sign(fixture.Release, fixture.Key));
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "private.pkcs8"), "Synthetic private-file sentinel.");
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "downloads.json"), JsonSerializer.Serialize(new DownloadManifest(1,
                [new("fixture.tar.gz", "linux-x64", "fixture", fixture.Release.Commit, new string('2', 64), bytes.Length, hash)]),
                DownloadCatalogue.JsonOptions));
            return fixture;
        }
        public void Dispose() { Key.Dispose(); Directory.Delete(Root, true); }
    }
}
