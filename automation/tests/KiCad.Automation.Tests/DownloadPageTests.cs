using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, TestCategory("LandingPage")]
public sealed class DownloadPageTests
{
    [TestMethod]
    [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "", "", "win-x64")]
    [DataRow("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", "", "", "mac")]
    [DataRow("Macintosh", "\"macOS\"", "\"arm\"", "osx-arm64")]
    [DataRow("Macintosh", "macOS", "x86", "osx-x64")]
    [DataRow("Linux x86_64", "", "", "linux-x64")]
    [DataRow("Linux aarch64", "Linux", "arm", "")]
    [DataRow("Windows NT 10.0; ARM64", "Windows", "arm", "")]
    [DataRow("iPad; Mobile; Macintosh", "macOS", "arm", "")]
    [DataRow("Android Linux x86_64 Mobile", "", "", "")]
    [DataRow("unknown", "", "", "")]
    public void RecommendationsDoNotGuessUnsupportedOrAmbiguousArchitectures(string ua, string platform, string arch, string expected)
        => Assert.AreEqual(expected, DownloadPage.DetectPlatform(ua, platform, arch));

    [TestMethod]
    public async Task BrowserHtmlAndMachineJsonShareRealDownloadsWithoutExposingSourceFiles()
    {
        string root = Directory.CreateTempSubdirectory("landing-http-").FullName;
        try
        {
            byte[] bytes = "Explicit synthetic package fixture"u8.ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var packages = new[] { "win-x64", "osx-arm64", "osx-x64", "linux-x64" }.Select((p, i) =>
                new DownloadArtifact(p + ".zip", p, "preview-" + i, new string('a', 40), hash, bytes.Length, hash)).ToList();
            packages.Add(new("source.zip", "source", "source", new string('a', 40), hash, bytes.Length, hash));
            foreach (var package in packages) await File.WriteAllBytesAsync(Path.Combine(root, package.FileName), bytes);
            await File.WriteAllTextAsync(Path.Combine(root, "downloads.json"), JsonSerializer.Serialize(new DownloadManifest(1, packages), DownloadCatalogue.JsonOptions));
            var catalogue = await DownloadCatalogue.LoadAsync(root);
            bool ready = false;
            await using var app = DownloadServer.Create(catalogue, "http://127.0.0.1:0", isReady: () => ready);
            await app.StartAsync();
            try
            {
                string origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new(origin) };
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/healthz")).StatusCode);
                ready = true;
                Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
                using var machine = await http.GetAsync("/");
                Assert.AreEqual("application/json", machine.Content.Headers.ContentType!.MediaType);
                Assert.AreEqual(await http.GetStringAsync("/downloads.json"), await machine.Content.ReadAsStringAsync());
                foreach (string theme in new[] { "dark", "light" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/?theme=" + theme + "&platform=win-x64");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                    using var response = await http.SendAsync(request);
                    Assert.AreEqual("text/html", response.Content.Headers.ContentType!.MediaType);
                    string html = await response.Content.ReadAsStringAsync();
                    StringAssert.Contains(html, "data-theme=\"" + theme + "\"");
                    StringAssert.Contains(html, "Download for Windows");
                    StringAssert.Contains(html, "https://github.com/holyglory/KAICad");
                    StringAssert.Contains(html, "/artifacts/win-x64.zip");
                    StringAssert.Contains(html, "/artifacts/osx-x64.zip");
                    StringAssert.Contains(html, "/artifacts/source.zip");
                    Assert.IsFalse(html.Contains("{{", StringComparison.Ordinal));
                }
                foreach (string name in new[] { "site.css", "site.js", "theme.js", "hero-dark.png", "hero-light.png", "inter.woff2", "apple.svg", "windows.svg", "linux.svg", "github.svg", "download.svg", "history.svg", "moon.svg", "sun.svg" })
                    Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/site/" + name)).StatusCode, name);
                foreach (string name in new[] { "hero-dark.png", "hero-light.png" })
                {
                    byte[] image = await http.GetByteArrayAsync("/site/" + name);
                    Assert.IsTrue(image.Length > 1000);
                    CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image[..8]);
                }
                foreach (string name in new[] { "DownloadPage.cs", "index.html", "../private", "preview-publisher.pkcs8" })
                    Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync("/site/" + name)).StatusCode);
                using var range = new HttpRequestMessage(HttpMethod.Get, "/artifacts/win-x64.zip");
                range.Headers.Range = new RangeHeaderValue(0, 3);
                using var part = await http.SendAsync(range);
                Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);
                CollectionAssert.AreEqual(bytes[..4], await part.Content.ReadAsByteArrayAsync());
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("/", new StringContent("no uploads"))).StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
