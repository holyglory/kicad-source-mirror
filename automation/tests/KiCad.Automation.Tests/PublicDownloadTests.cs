using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("ExternalIntegration")]
[TestCategory("PublicDownloads")]
public sealed class PublicDownloadTests
{
    [TestMethod]
    public async Task PublicHttpsDownloadsMatchFrozenArtifactsAndExposeNoControlOrPrivateFiles()
    {
        string origin = Environment.GetEnvironmentVariable("KICAD_PUBLIC_DOWNLOAD_URL")
            ?? throw new AssertFailedException("Select the authorized public download URL.");
        string root = Environment.GetEnvironmentVariable("KICAD_PACKAGE_CATALOGUE")
            ?? throw new AssertFailedException("Select the exact frozen public catalogue.");
        string evidence = Environment.GetEnvironmentVariable("KICAD_PUBLIC_DOWNLOAD_EVIDENCE")
            ?? throw new AssertFailedException("Select the dedicated public-download evidence directory.");
        var expected = await DownloadCatalogue.LoadAsync(root);
        var uri = new Uri(origin, UriKind.Absolute);
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("", uri.UserInfo);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var metadataResponse = await http.GetAsync("downloads.json", deadline.Token);
        Assert.AreEqual(HttpStatusCode.OK, metadataResponse.StatusCode);
        byte[] metadata = await metadataResponse.Content.ReadAsByteArrayAsync(deadline.Token);
        Assert.IsLessThanOrEqualTo(1024 * 1024, metadata.Length);
        var actual = JsonSerializer.Deserialize<DownloadManifest>(metadata, DownloadCatalogue.JsonOptions)!;
        Assert.AreEqual(expected.Manifest.SchemaVersion, actual.SchemaVersion);
        CollectionAssert.AreEqual(expected.Manifest.Artifacts.ToArray(), actual.Artifacts.ToArray());
        string scratch = Directory.CreateTempSubdirectory("kicad-public-download-").FullName;
        try
        {
            var downloads = await Task.WhenAll(expected.Manifest.Artifacts.Select(async artifact =>
            {
                string relative = "artifacts/" + Uri.EscapeDataString(artifact.FileName);
                using var head = new HttpRequestMessage(HttpMethod.Head, relative);
                using var headers = await http.SendAsync(head, deadline.Token);
                Assert.AreEqual(HttpStatusCode.OK, headers.StatusCode);
                Assert.AreEqual(artifact.Bytes, headers.Content.Headers.ContentLength);
                Assert.AreEqual("\"" + artifact.Sha256 + "\"", headers.Headers.ETag?.Tag);
                using var response = await http.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(artifact.Bytes, response.Content.Headers.ContentLength);
                Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
                string path = Path.Combine(scratch, artifact.FileName);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await response.Content.CopyToAsync(output, deadline.Token);
                Assert.AreEqual(artifact.Bytes, new FileInfo(path).Length);
                await using var input = File.OpenRead(path);
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, deadline.Token));
                Assert.AreEqual(artifact.Sha256, hash);
                input.Position = 0;
                byte[] prefix = new byte[32];
                await input.ReadExactlyAsync(prefix, deadline.Token);
                using var range = new HttpRequestMessage(HttpMethod.Get, relative);
                range.Headers.Range = new RangeHeaderValue(0, 31);
                using var partial = await http.SendAsync(range, deadline.Token);
                Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
                CollectionAssert.AreEqual(prefix, await partial.Content.ReadAsByteArrayAsync(deadline.Token));
                return new { artifact.FileName, artifact.Commit, artifact.Bytes, sha256 = hash, rangeVerified = true };
            }));
            var rejected = new Dictionary<string, int>();
            foreach (string path in new[] { ".git/config", "security-assumptions.md", "hardware.xml", "mcp", "api",
                "artifacts/private.txt", "artifacts/%2e%2e%2fsecurity-assumptions.md" })
            {
                using var response = await http.GetAsync(path, deadline.Token);
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
                rejected.Add(path, (int)response.StatusCode);
            }
            using var upload = await http.PostAsync("downloads.json", new StringContent("Synthetic prohibited upload."), deadline.Token);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, upload.StatusCode);
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "public-download.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, verifiedAtUtc = DateTimeOffset.UtcNow,
                origin = uri.AbsoluteUri, downloads, rejectedPaths = rejected,
                uploadStatus = (int)upload.StatusCode, qualifyingDelivery = false,
                automaticUpdating = false, nativeMacVerified = false
            }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
