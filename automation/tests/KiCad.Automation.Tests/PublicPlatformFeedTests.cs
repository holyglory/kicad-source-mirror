using System.Net;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("PublicDownloads")]
[TestCategory("ExternalIntegration")]
public sealed class PublicPlatformFeedTests
{
    [TestMethod]
    public async Task PublishedMacFeedsDownloadExactArchivesAndLeaveTheLegacyFeedUnchanged()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new AssertFailedException("Missing public verification input: " + name);
        string root = Required("KICAD_PACKAGE_CATALOGUE"), keyPath = Required("KICAD_UPDATE_PUBLISHER_SPKI_FILE");
        string evidence = Required("KICAD_PUBLIC_DOWNLOAD_EVIDENCE");
        var origin = new Uri(Required("KICAD_PUBLIC_DOWNLOAD_URL"));
        byte[] key = await File.ReadAllBytesAsync(keyPath);
        var catalogue = await DownloadCatalogue.LoadAsync(root);
        var feeds = await SignedUpdateCatalogue.LoadAsync(root, keyPath, catalogue);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var legacy = new UpdateDownloader(origin);
        byte[] rootBytes = await legacy.FetchManifestAsync("preview", deadline.Token);
        Assert.IsTrue(feeds.TryGet("preview", out var rootFeed));
        CollectionAssert.AreEqual(rootFeed!.CopyEnvelope(), rootBytes);
        PlatformUpdateFeeds.RequireLegacyCompatible(UpdateManifestCodec.Verify(rootBytes, key, "preview"));
        string scratch = Directory.CreateTempSubdirectory("kicad-public-platform-").FullName;
        try
        {
            var verifiedTargets = new List<object>();
            foreach (string platform in new[] { "osx-arm64", "osx-x64" })
            {
                Assert.IsTrue(feeds.TryGet(platform, "preview", out var expected), "The public Mac feed is required for this journey: " + platform);
                Uri platformOrigin = PlatformUpdateFeeds.PublisherBase(origin, platform);
                using var source = new UpdateDownloader(platformOrigin);
                byte[] envelope = await source.FetchManifestAsync("preview", deadline.Token);
                CollectionAssert.AreEqual(expected!.CopyEnvelope(), envelope);
                var manifest = UpdateManifestCodec.Verify(envelope, key, "preview");
                var download = await source.DownloadAsync(manifest, platform, "tar.gz", scratch, cancellationToken: deadline.Token);
                Assert.AreEqual(manifest.PayloadSha256, download.ManifestSha256);
                Assert.AreEqual(platform, download.Artifact.Platform);
                using var http = new HttpClient { BaseAddress = platformOrigin };
                var linux = catalogue.Manifest.Artifacts.First(x => x.Platform == "linux-x64");
                Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync("artifacts/" + linux.FileName, deadline.Token)).StatusCode);
                using var write = await http.PostAsync("updates/preview.json", new StringContent("not a write endpoint"), deadline.Token);
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, write.StatusCode);
                verifiedTargets.Add(new { platform, origin = platformOrigin.AbsoluteUri, manifest.Release.Commit,
                    manifest.Release.Version, manifest.Release.Sequence, download.Artifact.Sha256, download.Artifact.Bytes });
            }
            CollectionAssert.AreEqual(rootBytes, await legacy.FetchManifestAsync("preview", deadline.Token));
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "public-platform-feeds.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, verifiedAtUtc = DateTimeOffset.UtcNow, legacyFeedUnchanged = true,
                nativeFeeds = verifiedTargets, windowsFeedAvailable = feeds.TryGet("win-x64", "preview", out _),
                nativeMacExecution = false, automaticUpdatingQualified = false, qualifyingDelivery = false
            }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
