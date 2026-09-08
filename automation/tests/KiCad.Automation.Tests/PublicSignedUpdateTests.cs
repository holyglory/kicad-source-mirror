using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("ExternalIntegration")]
[TestCategory("PublicDownloads")]
public sealed class PublicSignedUpdateTests
{
    [TestMethod]
    public async Task PublicFeedAuthenticatesRealPackageAndInstalledHelperChecksSamePublisher()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new AssertFailedException("Missing explicit public verification input: " + name);
        var origin = new Uri(Required("KICAD_PUBLIC_DOWNLOAD_URL"));
        string root = Required("KICAD_PACKAGE_CATALOGUE");
        string evidence = Required("KICAD_PUBLIC_DOWNLOAD_EVIDENCE");
        string keyPath = Required("KICAD_UPDATE_PUBLISHER_SPKI_FILE");
        byte[] key = await File.ReadAllBytesAsync(keyPath);
        var catalogue = await DownloadCatalogue.LoadAsync(root);
        var expected = await SignedUpdateCatalogue.LoadAsync(root, keyPath, catalogue);
        Assert.IsTrue(expected.TryGet("preview", out var feed));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var source = new UpdateDownloader(origin);
        byte[] envelope = await source.FetchManifestAsync("preview", deadline.Token);
        CollectionAssert.AreEqual(feed!.CopyEnvelope(), envelope);
        var verified = UpdateManifestCodec.Verify(envelope, key, "preview");
        using (var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdateManifestCodec.Verify(envelope, wrongKey.ExportSubjectPublicKeyInfo(), "preview"));
        Assert.IsNull(verified.ForInstallation("osx-arm64", "zip"));
        Assert.IsNull(verified.ForInstallation("osx-x64", "zip"));

        string scratch = Directory.CreateTempSubdirectory("kicad-public-signed-").FullName;
        try
        {
            string state = Directory.CreateDirectory(Path.Combine(scratch, "state")).FullName;
            var checker = new UpdateChecker(source, state, key, envelope, "preview", "linux-x64", "tar.gz");
            Assert.AreEqual(UpdateAvailability.UpToDate, (await checker.CheckAsync(deadline.Token)).Availability);
            string accepted = Path.Combine(state, "accepted-envelope.json");
            DateTime unchanged = File.GetLastWriteTimeUtc(accepted);
            Assert.AreEqual(UpdateAvailability.UpToDate, (await checker.CheckAsync(deadline.Token)).Availability);
            Assert.AreEqual(unchanged, File.GetLastWriteTimeUtc(accepted), "An unchanged check must not rewrite metadata.");
            var downloaded = await source.DownloadAsync(verified, "linux-x64", "tar.gz", scratch,
                cancellationToken: deadline.Token);
            Assert.AreEqual(verified.ForInstallation("linux-x64", "tar.gz")!.Bytes, new FileInfo(downloaded.Path).Length);
            var installed = await LinuxVerifiedInstallation.InstallAsync(Path.Combine(scratch, "installed"),
                downloaded.Path, envelope, key, origin, "preview", deadline.Token);
            Assert.AreEqual(verified.Release.Commit, installed.Commit);

            // Invoke the actual self-contained helper shipped inside the public
            // archive, not a mock handler or this test assembly's command class.
            var start = new ProcessStartInfo(Path.Combine(installed.VersionDirectory, "kicad-mcp"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--check-update");
            start.ArgumentList.Add("--configuration");
            start.ArgumentList.Add(installed.ConfigurationPath);
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            JsonElement helperResult;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await stderr);
                string[] lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                using var terminal = JsonDocument.Parse(lines[^1]);
                helperResult = terminal.RootElement.Clone();
                Assert.AreEqual("up_to_date", helperResult.GetProperty("status").GetString());
                Assert.AreEqual(verified.Release.Commit, helperResult.GetProperty("commit").GetString());
                Assert.AreEqual(verified.PayloadSha256, helperResult.GetProperty("manifestSha256").GetString());
                Assert.IsFalse(helperResult.GetProperty("installationReady").GetBoolean());
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
            }

            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = origin };
            using var request = new HttpRequestMessage(HttpMethod.Head, "updates/preview.json");
            using var head = await http.SendAsync(request, deadline.Token);
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual(envelope.Length, head.Content.Headers.ContentLength);
            Assert.AreEqual("\"" + feed.Sha256 + "\"", head.Headers.ETag?.Tag);
            Assert.IsTrue(head.Headers.CacheControl?.NoCache);
            foreach (string path in new[] { "updates/stable.json", "updates/preview-publisher.pkcs8", "sign",
                "publisher-create", "preview-publisher.spki", "artifacts/preview-publisher.pkcs8" })
            {
                using var response = await http.GetAsync(path, deadline.Token);
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
            }
            using var upload = await http.PostAsync("updates/preview.json", new StringContent("Prohibited test upload."), deadline.Token);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, upload.StatusCode);
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "public-signed-update.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, verifiedAtUtc = DateTimeOffset.UtcNow, origin = origin.AbsoluteUri,
                publisherKeySha256 = verified.PublisherKeySha256, envelopeSha256 = feed.Sha256,
                manifestSha256 = verified.PayloadSha256, release = verified.Release, helperResult,
                authenticatedDownload = true, installedNativeIdentityVerified = true,
                installedPackagedHelperCheckedPublicFeed = true, unchangedCheckNoChurn = true,
                nativeEditorRestarted = false, publicCaptionUpdateVerified = false,
                automaticUpdatingQualified = false, nativeMacVerified = false, qualifyingDelivery = false
            }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
