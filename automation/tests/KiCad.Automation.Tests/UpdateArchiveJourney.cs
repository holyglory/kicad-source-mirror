using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using KiCad.Automation.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod]
    [TestCategory("NativeUpdateArchive")]
    public async Task AuthenticatedFrozenArchiveStagesAndRunsTheNativeRenderedJourney()
    {
        string catalogueRoot = Environment.GetEnvironmentVariable("KICAD_PACKAGE_CATALOGUE")
            ?? throw new AssertFailedException("Select the exact frozen package catalogue for this journey.");
        var catalogue = await DownloadCatalogue.LoadAsync(catalogueRoot);
        var package = catalogue.Manifest.Artifacts.Single(a => a.Platform == "linux-x64");
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(FindRoot(), "automation/artifacts"));
        string temporary = Directory.CreateTempSubdirectory("kicad-update-native-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var tlsKey = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", tlsKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = new UpdateRelease(1, "kicad-codex", "preview", 2, package.Version, package.Commit,
            [new("linux-x64", "tar.gz", package.FileName, package.Bytes, package.Sha256)]);
        byte[] published = UpdateManifestCodec.Sign(release, publisher);
        // Synthetic old-install receipt and publisher key are isolated test
        // inputs. The application archive itself is the real frozen candidate.
        byte[] installed = UpdateManifestCodec.Sign(release with { Sequence = 1, Version = "synthetic-prior-install" }, publisher);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/updates/preview.json", () => Results.Bytes(published, "application/json"));
        app.MapGet("/artifacts/" + package.FileName, () => Results.File(catalogue.Files[package.FileName].Path, "application/octet-stream"));
        try
        {
            await app.StartAsync(deadline.Token);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var source = new UpdateDownloader(new Uri(address + "/"), new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                    peer is not null && peer.RawData.AsSpan().SequenceEqual(certificate.RawData)
            });
            string state = Directory.CreateDirectory(Path.Combine(temporary, "state")).FullName;
            var check = await new UpdateChecker(source, state, publisher.ExportSubjectPublicKeyInfo(), installed,
                "preview", "linux-x64", "tar.gz").CheckAsync(deadline.Token);
            Assert.AreEqual(UpdateAvailability.Available, check.Availability);
            var downloaded = await source.DownloadAsync(check.Manifest, "linux-x64", "tar.gz", temporary, cancellationToken: deadline.Token);
            var staged = await LinuxUpdateStager.StageAsync(check.Manifest, downloaded, temporary, deadline.Token);
            File.Copy(Path.Combine(Path.GetDirectoryName(staged.Directory)!, "staging.json"), Path.Combine(evidence, "update-staging.json"));
            await File.WriteAllTextAsync(Path.Combine(evidence, "update-journey.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, package.Commit, package.Sha256, package.Bytes,
                manifestSha256 = check.Manifest.PayloadSha256, syntheticPublisher = true,
                publicFeed = false, activatedInstallation = false, qualifyingDelivery = false
            }, Evidence.JsonOptions), deadline.Token);
            await VerifyInstalledNative(Path.Combine(staged.Directory, "runtime"), evidence,
                Path.Combine(staged.Directory, "kicad-codex"), Path.Combine(staged.Directory, "kicad-mcp"));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(temporary, recursive: true);
        }
    }
}
