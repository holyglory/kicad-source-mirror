using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using KiCad.Automation.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PlatformUpdateFeedTests
{
    [TestMethod]
    public async Task RealHttpKeepsRootBytesAndIsolatesNativePlatformsWithoutWriteEndpoints()
    {
        using var fixture = new Fixture();
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        var feeds = await SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.Publisher, catalogue);
        await using var app = DownloadServer.Create(catalogue, "http://127.0.0.1:0", feeds);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Address(app)) };
            CollectionAssert.AreEqual(fixture.LegacyEnvelope, await http.GetByteArrayAsync("/updates/preview.json"));
            foreach (string platform in PlatformUpdateFeeds.NativePlatforms)
            {
                var artifact = fixture.Artifacts.Single(x => x.Platform == platform);
                string prefix = "/platforms/" + platform;
                using var response = await http.GetAsync(prefix + "/updates/preview.json");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var verified = UpdateManifestCodec.Verify(await response.Content.ReadAsByteArrayAsync(), fixture.Key.ExportSubjectPublicKeyInfo(), "preview");
                Assert.AreEqual(platform, verified.Release.Artifacts.Single().Platform);
                Assert.AreEqual(artifact.Commit, verified.Release.Commit);
                using var head = await http.SendAsync(new(HttpMethod.Head, prefix + "/updates/preview.json"));
                Assert.IsTrue(head.Content.Headers.ContentLength > 0);
                Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync());
                string package = prefix + "/artifacts/" + artifact.FileName;
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(fixture.Root, artifact.FileName)), await http.GetByteArrayAsync(package));
                using var range = new HttpRequestMessage(HttpMethod.Get, package);
                range.Headers.Range = new RangeHeaderValue(1, 4);
                using var partial = await http.SendAsync(range);
                Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
                Assert.AreEqual(4, (await partial.Content.ReadAsByteArrayAsync()).Length);
                foreach (var other in fixture.Artifacts.Where(x => x.Platform != platform))
                    Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(prefix + "/artifacts/" + other.FileName)).StatusCode);
                using var write = await http.PostAsync(prefix + "/updates/preview.json", new StringContent("not an upload"));
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, write.StatusCode);
            }
            foreach (string path in new[] { "/platforms/win-arm64/updates/preview.json", "/platforms/osx-arm64/updates/stable.json",
                "/platforms/osx-arm64/updates/private.json", "/platforms/osx-arm64/mcp", "/platforms/osx-arm64/control",
                "/platforms/osx-arm64/publisher.spki", "/platforms/osx-arm64/artifacts/private.json" })
                Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode, path);
            var mac = fixture.Artifacts.Single(x => x.Platform == "osx-arm64");
            File.AppendAllText(Path.Combine(fixture.Root, mac.FileName), "changed");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/platforms/osx-arm64/updates/preview.json")).StatusCode);
            CollectionAssert.AreEqual(fixture.LegacyEnvelope, await http.GetByteArrayAsync("/updates/preview.json"));
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    [DataRow("wrong-target")]
    [DataRow("wrong-key")]
    [DataRow("wrong-source")]
    [DataRow("wrong-package")]
    [DataRow("legacy-incompatible")]
    public async Task InvalidSignedFeedsAreRejectedBeforeServing(string fault)
    {
        using var fixture = new Fixture();
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        string path = Path.Combine(fixture.Root, PlatformUpdateFeeds.RelativeFeed("osx-arm64", "preview"));
        var release = fixture.Release("osx-arm64");
        switch (fault)
        {
            case "wrong-target": File.WriteAllBytes(path, fixture.Envelope("osx-x64")); break;
            case "wrong-key":
                using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                    File.WriteAllBytes(path, UpdateManifestCodec.Sign(release, other));
                break;
            case "wrong-source": File.WriteAllBytes(path, UpdateManifestCodec.Sign(release with { Commit = new string('f', 40) }, fixture.Key)); break;
            case "wrong-package": File.WriteAllBytes(path, UpdateManifestCodec.Sign(release with
                { Artifacts = [release.Artifacts[0] with { Sha256 = new string('f', 64) }] }, fixture.Key)); break;
            case "legacy-incompatible": File.WriteAllBytes(Path.Combine(fixture.Root, "updates/preview.json"), fixture.Envelope("osx-arm64")); break;
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.Publisher, catalogue));
    }

    [TestMethod]
    public async Task LinkedPlatformDirectoryCannotExposeUnrelatedFiles()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Symbolic-link fixture uses Unix permissions."); return; }
        using var fixture = new Fixture();
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        string target = Path.Combine(fixture.Root, "updates/platforms/osx-arm64"), saved = target + ".retained";
        Directory.Move(target, saved);
        Directory.CreateSymbolicLink(target, saved);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.Publisher, catalogue));
        Directory.Delete(target);
        Directory.Move(saved, target);
        Assert.IsTrue((await SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.Publisher, catalogue)).TryGet("osx-arm64", "preview", out _));
    }

    [TestMethod]
    public async Task PublicationAdvancesOnlySelectedFeedsAndUnchangedRequestsProduceNoChurn()
    {
        using var fixture = new Fixture();
        string declarationRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "operator-inputs")).FullName;
        var sources = new List<PlatformFeedSource>();
        foreach (string platform in new[] { "osx-arm64", "win-x64" })
        {
            string path = Path.Combine(declarationRoot, platform + ".json");
            await File.WriteAllBytesAsync(path, fixture.Envelope(platform, 3));
            sources.Add(new(platform, "preview", path));
        }
        string output = fixture.Root + "-new";
        try
        {
            var request = new PlatformFeedStageRequest(fixture.Root, output, fixture.Publisher, sources);
            var staged = await SignedPlatformFeedStaging.RunAsync(request, CancellationToken.None);
            Assert.AreEqual("staged", staged.Status);
            Assert.AreEqual(2, staged.ChangedFeeds);
            Assert.IsFalse(staged.QualifyingDelivery);
            CollectionAssert.AreEqual(fixture.LegacyEnvelope, File.ReadAllBytes(Path.Combine(output, "updates/preview.json")));
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(fixture.Root, PlatformUpdateFeeds.RelativeFeed("osx-x64", "preview"))),
                File.ReadAllBytes(Path.Combine(output, PlatformUpdateFeeds.RelativeFeed("osx-x64", "preview"))));
            Assert.IsFalse(File.Exists(Path.Combine(output, "publisher.spki")));
            Assert.IsFalse(Directory.Exists(Path.Combine(output, "operator-inputs")));
            var downloads = await DownloadCatalogue.LoadAsync(output);
            var feeds = await SignedUpdateCatalogue.LoadAsync(output, fixture.Publisher, downloads);
            Assert.IsTrue(feeds.TryGet("win-x64", "preview", out var windows));
            Assert.AreEqual(3L, windows!.Manifest.Release.Sequence);
            string unchangedOutput = output + "-unchanged";
            var unchanged = await SignedPlatformFeedStaging.RunAsync(request with { Previous = output, Output = unchangedOutput }, CancellationToken.None);
            Assert.AreEqual("unchanged", unchanged.Status);
            Assert.IsFalse(Directory.Exists(unchangedOutput));
            byte[] retainedFeed = File.ReadAllBytes(Path.Combine(output, PlatformUpdateFeeds.RelativeFeed("win-x64", "preview")));
            await File.WriteAllBytesAsync(sources[1].EnvelopePath, fixture.Envelope("win-x64", 2));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedPlatformFeedStaging.RunAsync(request with
                { Previous = output, Output = unchangedOutput }, CancellationToken.None));
            CollectionAssert.AreEqual(retainedFeed, File.ReadAllBytes(Path.Combine(output, PlatformUpdateFeeds.RelativeFeed("win-x64", "preview"))));
            await Assert.ThrowsAsync<OperationCanceledException>(() => SignedPlatformFeedStaging.RunAsync(request with { Output = unchangedOutput }, new CancellationToken(true)));
            Assert.IsFalse(Directory.Exists(unchangedOutput));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [TestMethod]
    public async Task NewPlatformFeedCanBeAddedButMismatchedOrDuplicateDeclarationsCannotPublish()
    {
        using var fixture = new Fixture();
        string original = Path.Combine(fixture.Root, PlatformUpdateFeeds.RelativeFeed("osx-arm64", "preview"));
        byte[] bytes = File.ReadAllBytes(original);
        File.Delete(original);
        string input = Path.Combine(fixture.Root, "incoming.json");
        await File.WriteAllBytesAsync(input, bytes);
        string output = fixture.Root + "-add";
        var source = new PlatformFeedSource("osx-arm64", "preview", input);
        var request = new PlatformFeedStageRequest(fixture.Root, output, fixture.Publisher, [source]);
        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => SignedPlatformFeedStaging.RunAsync(request with { Feeds = [source, source] }, CancellationToken.None));
            Assert.IsFalse(Directory.Exists(output));
            await File.WriteAllBytesAsync(input, fixture.Envelope("win-x64"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedPlatformFeedStaging.RunAsync(request, CancellationToken.None));
            Assert.IsFalse(Directory.Exists(output));
            await File.WriteAllBytesAsync(input, bytes);
            var result = await SignedPlatformFeedStaging.RunAsync(request, CancellationToken.None);
            Assert.AreEqual(1, result.ChangedFeeds);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(output, PlatformUpdateFeeds.RelativeFeed("osx-arm64", "preview"))));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    [TestMethod]
    public async Task CompiledDownloaderUsesPlatformBaseOverHttpsWithoutChangingPublisherTrust()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var tls = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", tls, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var catalogue = await DownloadCatalogue.LoadAsync(fixture.Root);
        var feeds = await SignedUpdateCatalogue.LoadAsync(fixture.Root, fixture.Publisher, catalogue);
        await using var app = DownloadServer.Create(catalogue, "https://127.0.0.1:0", feeds,
            server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await app.StartAsync();
        try
        {
            foreach (string platform in PlatformUpdateFeeds.NativePlatforms)
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    // Trust only this generated certificate, only for this fixture's connection.
                    ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null
                        && presented.RawData.AsSpan().SequenceEqual(certificate.RawData)
                };
                Uri origin = PlatformUpdateFeeds.PublisherBase(new Uri(Address(app)), platform);
                using var downloader = new UpdateDownloader(origin, handler);
                byte[] envelope = await downloader.FetchManifestAsync("preview", deadline.Token);
                var verified = UpdateManifestCodec.Verify(envelope, fixture.Key.ExportSubjectPublicKeyInfo(), "preview");
                string format = platform == "win-x64" ? "zip" : "tar.gz";
                var download = await downloader.DownloadAsync(verified, platform, format, fixture.Root, cancellationToken: deadline.Token);
                Assert.AreEqual(platform, download.Artifact.Platform);
                Assert.AreEqual(verified.PayloadSha256, download.ManifestSha256);
                Assert.ThrowsExactly<InvalidDataException>(() => UpdateManifestCodec.Verify(fixture.Envelope(platform, 1),
                    fixture.Key.ExportSubjectPublicKeyInfo(), "preview", new(verified.Release.Sequence, verified.PayloadSha256)));
            }
        }
        finally { await app.StopAsync(); }
    }

    private static string Address(Microsoft.AspNetCore.Builder.WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + "/";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kicad-platform-feeds-").FullName;
        public string Publisher => Path.Combine(Root, "publisher.spki");
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public List<DownloadArtifact> Artifacts { get; } = new();
        public byte[] LegacyEnvelope { get; }
        public Fixture()
        {
            int index = 1;
            foreach (string platform in new[] { "linux-x64", "osx-arm64", "osx-x64", "win-x64" })
            {
                string format = platform == "win-x64" ? "zip" : "tar.gz";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes("Synthetic " + platform + " package fixture.");
                string file = platform + "." + format;
                File.WriteAllBytes(Path.Combine(Root, file), bytes);
                Artifacts.Add(new(file, platform, "fixture-" + platform, new string((char)('0' + index++), 40),
                    new string('a', 64), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            Directory.CreateDirectory(Path.Combine(Root, "updates"));
            LegacyEnvelope = Envelope("linux-x64");
            File.WriteAllBytes(Path.Combine(Root, "updates/preview.json"), LegacyEnvelope);
            foreach (string platform in PlatformUpdateFeeds.NativePlatforms)
            {
                string path = Path.Combine(Root, PlatformUpdateFeeds.RelativeFeed(platform, "preview"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Envelope(platform));
            }
            File.WriteAllBytes(Publisher, Key.ExportSubjectPublicKeyInfo());
            File.WriteAllText(Path.Combine(Root, "downloads.json"), JsonSerializer.Serialize(new DownloadManifest(1, Artifacts), DownloadCatalogue.JsonOptions));
        }
        public UpdateRelease Release(string platform, long sequence = 2)
        {
            var file = Artifacts.Single(x => x.Platform == platform);
            return new(1, "kicad-codex", "preview", sequence, file.Version, file.Commit,
                [new(platform, platform == "win-x64" ? "zip" : "tar.gz", file.FileName, file.Bytes, file.Sha256)]);
        }
        public byte[] Envelope(string platform, long sequence = 2) => UpdateManifestCodec.Sign(Release(platform, sequence), Key);
        public void Dispose() { Key.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
