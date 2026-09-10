using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SignedLinuxPreviewStagingTests
{
    [TestMethod]
    public async Task NewSignedLinuxFeedPreservesMacDownloadsOtherChannelsAndInputTrees()
    {
        using var fixture = new Fixture();
        byte[] previous = File.ReadAllBytes(Path.Combine(fixture.Previous, "updates/preview.json"));
        byte[] stable = File.ReadAllBytes(Path.Combine(fixture.Previous, "updates/stable.json"));
        var result = await SignedLinuxPreviewStaging.RunAsync(fixture.Request, CancellationToken.None);
        Assert.AreEqual("staged", result.Status);
        Assert.AreEqual(4, result.ArtifactCount);
        Assert.AreEqual(2L, result.Sequence);
        Assert.IsFalse(result.QualifyingDelivery);
        foreach (string name in new[] { "old-mac.tar.gz", "old-linux.tar.gz", "new-linux.tar.gz", "new-source.tar.gz" })
            Assert.IsTrue(File.Exists(Path.Combine(result.Directory, name)), name);
        CollectionAssert.AreEqual(File.ReadAllBytes(fixture.Request.Feed), File.ReadAllBytes(Path.Combine(result.Directory, "updates/preview.json")));
        CollectionAssert.AreEqual(stable, File.ReadAllBytes(Path.Combine(result.Directory, "updates/stable.json")));
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(Path.Combine(fixture.Previous, "updates/preview.json")));
        Assert.IsFalse(File.Exists(Path.Combine(result.Directory, "private-key.pkcs8")));
        Assert.IsFalse(File.Exists(Path.Combine(result.Directory, "private-evidence.json")));
        var manifest = JsonSerializer.Deserialize<DownloadManifest>(File.ReadAllText(Path.Combine(result.Directory, "downloads.json")), Fixture.Json)!;
        Assert.AreEqual(4, manifest.Artifacts.Count);
        Assert.AreEqual(result.CatalogueSha256, Hash(File.ReadAllBytes(Path.Combine(result.Directory, "downloads.json"))));
    }

    [TestMethod]
    [DataRow("replay")]
    [DataRow("publisher")]
    [DataRow("changed-package")]
    [DataRow("wrong-source")]
    [DataRow("source-hash")]
    [DataRow("feed-package")]
    [DataRow("filename-collision")]
    [DataRow("wrong-platform")]
    [DataRow("nested-output")]
    public async Task InvalidPublicationNeverChangesThePreviousRoot(string fault)
    {
        using var fixture = new Fixture();
        byte[] oldCatalogue = File.ReadAllBytes(Path.Combine(fixture.Previous, "downloads.json"));
        byte[] oldFeed = File.ReadAllBytes(Path.Combine(fixture.Previous, "updates/preview.json"));
        var request = fixture.Request;
        switch (fault)
        {
            case "replay": fixture.WriteFeed(fixture.Release with { Sequence = 1 }); break;
            case "publisher":
                using (var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                    File.WriteAllBytes(request.Publisher, wrong.ExportSubjectPublicKeyInfo());
                break;
            case "changed-package": File.AppendAllText(Path.Combine(fixture.Candidate, fixture.App.FileName), "changed"); break;
            case "wrong-source": request = request with { Commit = new string('b', 40) }; break;
            case "source-hash": fixture.WriteCandidate(fixture.App with { SourceSha256 = new string('b', 64) }); break;
            case "feed-package": fixture.WriteFeed(fixture.Release with { Artifacts = [fixture.Release.Artifacts[0] with { Sha256 = new string('b', 64) }] }); break;
            case "filename-collision":
                File.Copy(Path.Combine(fixture.Candidate, fixture.App.FileName), Path.Combine(fixture.Candidate, "old-linux.tar.gz"));
                fixture.WriteCandidate(fixture.App with { FileName = "old-linux.tar.gz" });
                fixture.WriteFeed(fixture.Release with { Artifacts = [fixture.Release.Artifacts[0] with { FileName = "old-linux.tar.gz" }] });
                break;
            case "wrong-platform": fixture.WriteCandidate(fixture.App with { Platform = "osx-x64" }); break;
            case "nested-output": request = request with { Output = Path.Combine(fixture.Previous, "nested") }; break;
        }
        if (fault == "nested-output")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => SignedLinuxPreviewStaging.RunAsync(request, CancellationToken.None));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SignedLinuxPreviewStaging.RunAsync(request, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(request.Output));
        CollectionAssert.AreEqual(oldCatalogue, File.ReadAllBytes(Path.Combine(fixture.Previous, "downloads.json")));
        CollectionAssert.AreEqual(oldFeed, File.ReadAllBytes(Path.Combine(fixture.Previous, "updates/preview.json")));
    }

    [TestMethod]
    public async Task CancellationExistingOutputAndRetryPreserveWork()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SignedLinuxPreviewStaging.RunAsync(fixture.Request, new CancellationToken(true)));
        Assert.IsFalse(Directory.Exists(fixture.Request.Output));
        Directory.CreateDirectory(fixture.Request.Output);
        string owned = Path.Combine(fixture.Request.Output, "user-work");
        File.WriteAllText(owned, "preserve");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => SignedLinuxPreviewStaging.RunAsync(fixture.Request, CancellationToken.None));
        Assert.AreEqual("preserve", File.ReadAllText(owned));
        var next = fixture.Request with { Output = Path.Combine(fixture.Root, "retry") };
        var result = await SignedLinuxPreviewStaging.RunAsync(next, CancellationToken.None);
        Assert.AreEqual("staged", result.Status);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Fixture : IDisposable
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public string Root { get; } = Directory.CreateTempSubdirectory("kicad-signed-stage-").FullName;
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Candidate { get; }
        public string Previous { get; }
        public DownloadArtifact App { get; }
        public DownloadArtifact Source { get; }
        public UpdateRelease Release { get; }
        public SignedLinuxPreviewRequest Request { get; }

        public Fixture()
        {
            Candidate = Directory.CreateDirectory(Path.Combine(Root, "candidate")).FullName;
            Previous = Directory.CreateDirectory(Path.Combine(Root, "previous")).FullName;
            string updates = Directory.CreateDirectory(Path.Combine(Previous, "updates")).FullName;
            const string version = "fixture-new";
            string commit = new('a', 40);
            Source = WriteArtifact(Candidate, "new-source.tar.gz", "source", version, commit, "Synthetic source fixture.");
            App = WriteArtifact(Candidate, "new-linux.tar.gz", "linux-x64", version, commit, "Synthetic Linux fixture.") with { SourceSha256 = Source.Sha256 };
            var old = WriteArtifact(Previous, "old-linux.tar.gz", "linux-x64", "fixture-old", new string('c', 40), "Synthetic old Linux fixture.");
            var mac = WriteArtifact(Previous, "old-mac.tar.gz", "osx-arm64", "fixture-mac", new string('d', 40), "Synthetic Mac fixture.");
            File.WriteAllText(Path.Combine(Previous, "downloads.json"), JsonSerializer.Serialize(new DownloadManifest(1, [old, mac]), Json));
            var baseline = new UpdateRelease(1, "kicad-codex", "preview", 1, old.Version, old.Commit,
                [new(old.Platform, "tar.gz", old.FileName, old.Bytes, old.Sha256)]);
            File.WriteAllBytes(Path.Combine(updates, "preview.json"), UpdateManifestCodec.Sign(baseline, key));
            File.WriteAllBytes(Path.Combine(updates, "stable.json"), UpdateManifestCodec.Sign(baseline with { Channel = "stable" }, key));
            Release = new(1, "kicad-codex", "preview", 2, version, commit,
                [new("linux-x64", "tar.gz", App.FileName, App.Bytes, App.Sha256)]);
            Request = new(Candidate, commit, Previous, Path.Combine(Root, "output"), Path.Combine(Root, "signed.json"), Path.Combine(Root, "publisher.spki"));
            File.WriteAllBytes(Request.Publisher, key.ExportSubjectPublicKeyInfo());
            File.WriteAllText(Path.Combine(Candidate, "private-key.pkcs8"), "Synthetic private marker; not a key.");
            File.WriteAllText(Path.Combine(Previous, "private-evidence.json"), "Synthetic private marker.");
            WriteCandidate(App);
            WriteFeed(Release);
        }

        public void WriteCandidate(DownloadArtifact app) => File.WriteAllText(Path.Combine(Candidate, "downloads.json"),
            JsonSerializer.Serialize(new DownloadManifest(1, [app, Source]), Json));
        public void WriteFeed(UpdateRelease release) => File.WriteAllBytes(Request.Feed, UpdateManifestCodec.Sign(release, key));
        private static DownloadArtifact WriteArtifact(string root, string name, string platform, string version, string commit, string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(Path.Combine(root, name), bytes);
            return new(name, platform, version, commit, Hash(bytes), bytes.Length, Hash(bytes));
        }
        public void Dispose() { key.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
