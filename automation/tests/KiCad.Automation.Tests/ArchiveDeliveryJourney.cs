using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using KiCad.Automation.Downloads;
using KiCad.Automation.Validation;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod]
    [TestCategory("NativePackageArchive")]
    public async Task DownloadedArchiveLaunchesNativeEditorAndMcp()
    {
        string directory = Environment.GetEnvironmentVariable("KICAD_PACKAGE_CATALOGUE")
            ?? throw new AssertFailedException("Select the exact frozen package catalogue for this journey.");
        var catalogue = await DownloadCatalogue.LoadAsync(directory);
        var package = catalogue.Manifest.Artifacts.Single(a => a.Platform == "linux-x64");
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(FindRoot(), "automation/artifacts"));
        string temporary = Directory.CreateTempSubdirectory("kicad archive review-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var app = DownloadServer.Create(catalogue, "http://127.0.0.1:0");
        try
        {
            await app.StartAsync(deadline.Token);
            string address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            foreach (var artifact in catalogue.Manifest.Artifacts)
            {
                string path = Path.Combine(temporary, artifact.FileName);
                using var response = await http.GetAsync("/artifacts/" + artifact.FileName,
                    HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    await response.Content.CopyToAsync(output, deadline.Token);
                Assert.AreEqual(artifact.Bytes, new FileInfo(path).Length);
                Assert.AreEqual(artifact.Sha256, Evidence.Hash(path), artifact.FileName);
            }
            string extracted = Directory.CreateDirectory(Path.Combine(temporary, "extracted package")).FullName;
            await using (var compressed = File.OpenRead(Path.Combine(temporary, package.FileName)))
            await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, extracted, overwriteFiles: false, deadline.Token);
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(extracted, "package.json")));
            Assert.AreEqual(package.Commit, metadata.RootElement.GetProperty("commit").GetString());
            Assert.AreEqual(package.SourceSha256, metadata.RootElement.GetProperty("sourceSha256").GetString());
            Assert.IsFalse(metadata.RootElement.GetProperty("qualifyingDelivery").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(evidence, "downloaded-package.json"),
                JsonSerializer.Serialize(package, Evidence.JsonOptions), deadline.Token);
            await VerifyInstalledNative(Path.Combine(extracted, "runtime"), evidence,
                Path.Combine(extracted, "kicad-codex"), Path.Combine(extracted, "kicad-mcp"));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(temporary, recursive: true);
        }
    }
}
