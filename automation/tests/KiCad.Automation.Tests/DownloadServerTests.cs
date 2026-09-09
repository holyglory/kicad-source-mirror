using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Downloads;
using KiCad.Automation.Distribution;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DownloadServerTests
{
    [TestMethod]
    [DataRow("linux-x64")]
    [DataRow("osx-arm64")]
    [DataRow("osx-x64")]
    [DataRow("win-x64")]
    public async Task RealHttpServesOnlyListedBytesWithHeadAndResume(string platform)
    {
        string root = Directory.CreateTempSubdirectory("kicad-download-fixture-").FullName;
        try
        {
            byte[] content = "Synthetic package fixture; not a real application."u8.ToArray();
            var artifact = await WriteFixture(root, content, platform);
            await File.WriteAllTextAsync(Path.Combine(root, "private.txt"), "must not be served");
            DownloadCatalogue catalogue = await DownloadCatalogue.LoadAsync(root);
            await using var app = DownloadServer.Create(catalogue, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                string address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address) };
                string path = "/artifacts/" + artifact.FileName;
                using var response = await http.GetAsync(path);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(content, await response.Content.ReadAsByteArrayAsync());
                Assert.AreEqual("\"" + artifact.Sha256 + "\"", response.Headers.ETag!.Tag);
                using var head = await http.SendAsync(new(HttpMethod.Head, path));
                Assert.AreEqual(content.Length, head.Content.Headers.ContentLength);
                Assert.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length);
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path);
                rangeRequest.Headers.Range = new RangeHeaderValue(2, 6);
                using var range = await http.SendAsync(rangeRequest);
                Assert.AreEqual(HttpStatusCode.PartialContent, range.StatusCode);
                CollectionAssert.AreEqual(content[2..7], await range.Content.ReadAsByteArrayAsync());
                foreach (string hidden in new[] { "/private.txt", "/artifacts/private.txt",
                    "/artifacts/%2e%2e%2fprivate.txt", "/mcp", "/control" })
                    Assert.IsFalse((await http.GetAsync(hidden)).IsSuccessStatusCode, hidden);
                using var write = await http.PostAsync(path, new ByteArrayContent(content));
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, write.StatusCode);
                var served = JsonSerializer.Deserialize<DownloadManifest>(
                    await http.GetStringAsync("/downloads.json"), DownloadCatalogue.JsonOptions)!;
                Assert.AreEqual(artifact, served.Artifacts.Single());
                await File.AppendAllTextAsync(Path.Combine(root, artifact.FileName), "changed");
                using var changed = await http.GetAsync(path);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, changed.StatusCode);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DownloadCatalogue.LoadAsync(root));
                await WriteFixture(root, content, platform);
                Assert.IsNotNull(await DownloadCatalogue.LoadAsync(root),
                    "Restoring the exact package and revalidating must recover catalogue loading.");
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CatalogueRejectsInvalidIdentityUnlistedTypesAndChangedBytes()
    {
        string root = Directory.CreateTempSubdirectory("kicad-catalogue-fixture-").FullName;
        try
        {
            var artifact = await WriteFixture(root, "Synthetic fixture bytes."u8.ToArray());
            foreach (var invalid in new[]
            {
                artifact with { FileName = null! },
                artifact with { FileName = "../fixture.zip" },
                artifact with { FileName = "design.kicad_sch" },
                artifact with { Platform = "any" },
                artifact with { Platform = "win-arm64" },
                artifact with { Commit = "main" },
                artifact with { SourceSha256 = "unknown" },
                artifact with { Sha256 = new string('0', 64) },
                artifact with { Bytes = artifact.Bytes + 1 }
            })
            {
                await Manifest(new(1, [invalid]));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DownloadCatalogue.LoadAsync(root));
            }
            foreach (var invalid in new[]
            {
                new DownloadManifest(2, [artifact]), new DownloadManifest(1, []),
                new DownloadManifest(1, [artifact, artifact])
            })
            {
                await Manifest(invalid);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DownloadCatalogue.LoadAsync(root));
            }
            await Manifest(new(1, [artifact]));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                DownloadCatalogue.LoadAsync(root, new CancellationToken(canceled: true)));
            Assert.IsNotNull(await DownloadCatalogue.LoadAsync(root));
            if (!OperatingSystem.IsWindows())
            {
                File.Move(Path.Combine(root, artifact.FileName), Path.Combine(root, "target"));
                File.CreateSymbolicLink(Path.Combine(root, artifact.FileName), "target");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DownloadCatalogue.LoadAsync(root));
            }

            Task Manifest(DownloadManifest manifest) => File.WriteAllTextAsync(Path.Combine(root, "downloads.json"),
                JsonSerializer.Serialize(manifest, DownloadCatalogue.JsonOptions));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<DownloadArtifact> WriteFixture(string root, byte[] content, string platform = "linux-x64")
    {
        var artifact = new DownloadArtifact("synthetic-fixture.zip", platform, "synthetic-test",
            new string('1', 40), new string('2', 64), content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)));
        await File.WriteAllBytesAsync(Path.Combine(root, artifact.FileName), content);
        await File.WriteAllTextAsync(Path.Combine(root, "downloads.json"),
            JsonSerializer.Serialize(new DownloadManifest(1, [artifact]), DownloadCatalogue.JsonOptions));
        return artifact;
    }
}
