using System.Net;
using System.Security.Cryptography;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateCheckerTests
{
    [TestMethod]
    public async Task RestartKeepsAcceptedReleaseAndUnchangedChecksDoNotRewriteIt()
    {
        using var fixture = new FeedFixture();
        var result = await fixture.Checker().CheckAsync();
        Assert.AreEqual(UpdateAvailability.Available, result.Availability);
        Assert.AreEqual(3L, result.Manifest.Release.Sequence);
        byte[] saved = await File.ReadAllBytesAsync(fixture.AcceptedPath);
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.AcceptedPath, timestamp);
        await fixture.Checker().CheckAsync();
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(fixture.AcceptedPath));
        fixture.Payload = fixture.Envelope(2);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
        fixture.Payload = fixture.Envelope(3, version: "conflicting-release");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
        CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(fixture.AcceptedPath));
        fixture.Payload = fixture.Envelope(4);
        Assert.AreEqual(4L, (await fixture.Checker().CheckAsync()).Manifest.Release.Sequence);
        Assert.IsFalse(Directory.GetFiles(fixture.Root).Any(path => path.EndsWith(".partial")));
    }

    [TestMethod]
    public async Task InstalledBaselineProtectsFirstCheckAndAllowsAlreadyInstalledRelease()
    {
        using var fixture = new FeedFixture();
        fixture.Payload = fixture.Envelope(1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
        Assert.IsFalse(File.Exists(fixture.AcceptedPath));
        fixture.Payload = fixture.Installed;
        Assert.AreEqual(UpdateAvailability.UpToDate, (await fixture.Checker().CheckAsync()).Availability);
        // An authenticated older cache after installing a newer application is
        // harmless, but cannot lower that application's packaged baseline.
        await File.WriteAllBytesAsync(fixture.AcceptedPath, fixture.Envelope(1));
        Assert.AreEqual(UpdateAvailability.UpToDate, (await fixture.Checker().CheckAsync()).Availability);
        await File.WriteAllBytesAsync(fixture.AcceptedPath, fixture.Envelope(2, version: "conflict"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
    }

    [TestMethod]
    [DataRow("osx-arm64", "zip")]
    [DataRow("osx-arm64", "tar.gz")]
    [DataRow("osx-x64", "tar.gz")]
    [DataRow("win-x64", "zip")]
    public async Task MissingPlatformDoesNotOfferAnotherArchitecturesPackage(string platform, string format)
    {
        using var fixture = new FeedFixture();
        fixture.Payload = fixture.Envelope(3, platform: platform, format: format);
        var result = await fixture.Checker().CheckAsync();
        Assert.AreEqual(UpdateAvailability.TargetUnavailable, result.Availability);
        Assert.IsNull(result.Manifest.ForInstallation("linux-x64", "tar.gz"));
        fixture.Payload = fixture.Envelope(4);
        Assert.AreEqual(UpdateAvailability.Available, (await fixture.Checker().CheckAsync()).Availability);
    }

    [TestMethod]
    public async Task BadSavedStateIsPreservedAndNeverSilentlyReset()
    {
        using var fixture = new FeedFixture();
        foreach (byte[] invalid in new[] { "broken"u8.ToArray(), new byte[UpdateManifestCodec.MaximumEnvelopeBytes + 1] })
        {
            await File.WriteAllBytesAsync(fixture.AcceptedPath, invalid);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
            Assert.AreEqual(0, fixture.Requests);
            CollectionAssert.AreEqual(invalid, await File.ReadAllBytesAsync(fixture.AcceptedPath));
        }
        using var other = new FeedFixture();
        await File.WriteAllBytesAsync(fixture.AcceptedPath, other.Payload);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
        CollectionAssert.AreEqual(other.Payload, await File.ReadAllBytesAsync(fixture.AcceptedPath));
    }

    [TestMethod]
    public async Task PersistenceFailureDoesNotReportAvailabilityAndRetryCanRecover()
    {
        using var fixture = new FeedFixture();
        fixture.BeforeResponse = (_, _) =>
        {
            Directory.CreateDirectory(fixture.AcceptedPath);
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Checker().CheckAsync());
        Assert.IsFalse(Directory.GetFiles(fixture.Root).Any(path => path.EndsWith(".partial")));
        Assert.IsTrue(Directory.Exists(fixture.AcceptedPath));
        Directory.Delete(fixture.AcceptedPath);
        fixture.BeforeResponse = null;
        Assert.AreEqual(UpdateAvailability.Available, (await fixture.Checker().CheckAsync()).Availability);
    }

    [TestMethod]
    public async Task CancellationReleasesOwnershipAndAnotherCheckerCannotCompete()
    {
        using var fixture = new FeedFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeResponse = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<UpdateCheckResult> first = fixture.Checker().CheckAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => fixture.Checker().CheckAsync());
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.IsFalse(File.Exists(fixture.AcceptedPath));
        fixture.BeforeResponse = null;
        Assert.AreEqual(UpdateAvailability.Available, (await fixture.Checker().CheckAsync()).Availability);
    }

    [TestMethod]
    public async Task FailedOrOversizedFeedNeverReplacesAcceptedMetadata()
    {
        using var fixture = new FeedFixture();
        await fixture.Checker().CheckAsync();
        byte[] accepted = await File.ReadAllBytesAsync(fixture.AcceptedPath);
        foreach (string fault in new[] { "size", "stream-size", "redirect", "encoding", "status", "signature" })
        {
            fixture.ResponseFault = fault;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Checker().CheckAsync());
            CollectionAssert.AreEqual(accepted, await File.ReadAllBytesAsync(fixture.AcceptedPath));
        }
        fixture.ResponseFault = null;
        fixture.BeforeResponse = (_, _) => throw new HttpRequestException("Synthetic offline publisher.");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => fixture.Checker().CheckAsync());
        CollectionAssert.AreEqual(accepted, await File.ReadAllBytesAsync(fixture.AcceptedPath));
        fixture.BeforeResponse = null;
        Assert.AreEqual(UpdateAvailability.Available, (await fixture.Checker().CheckAsync()).Availability);
    }

    private sealed class FeedFixture : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly UpdateDownloader source;
        public string Root { get; } = Directory.CreateTempSubdirectory("kicad-update-check-").FullName;
        public string AcceptedPath => Path.Combine(Root, "accepted-envelope.json");
        public byte[] Installed { get; }
        public byte[] Payload { get; set; }
        public string? ResponseFault { get; set; }
        public int Requests { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse { get; set; }

        public FeedFixture()
        {
            Installed = Envelope(2);
            Payload = Envelope(3);
            source = new(new Uri("https://updates.example.test/releases/"), new Handler(async (request, token) =>
            {
                Requests++;
                Assert.AreEqual("https://updates.example.test/releases/updates/preview.json", request.RequestUri!.AbsoluteUri);
                if (BeforeResponse is not null) await BeforeResponse(request, token);
                byte[] bytes = ResponseFault switch
                {
                    "size" or "stream-size" => new byte[UpdateManifestCodec.MaximumEnvelopeBytes + 1],
                    "signature" => "{\"schemaVersion\":1,\"payload\":\"\",\"signature\":\"\"}"u8.ToArray(),
                    _ => Payload
                };
                var response = new HttpResponseMessage(ResponseFault == "status" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = ResponseFault == "stream-size" ? new StreamContent(new NonSeekable(bytes)) : new ByteArrayContent(bytes)
                };
                if (ResponseFault == "redirect") response.Headers.Location = new Uri("https://elsewhere.example.test/");
                if (ResponseFault == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            }));
        }

        public UpdateChecker Checker() => new(source, Root, key.ExportSubjectPublicKeyInfo(), Installed,
            "preview", "linux-x64", "tar.gz");

        public byte[] Envelope(long sequence, string? version = null, string platform = "linux-x64", string format = "tar.gz") =>
            UpdateManifestCodec.Sign(new(1, "kicad-codex", "preview", sequence, version ?? "preview-" + sequence,
                new string('1', 40), [new(platform, format, "fixture." + format, 10, new string('2', 64))]), key);

        public void Dispose() { source.Dispose(); key.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
