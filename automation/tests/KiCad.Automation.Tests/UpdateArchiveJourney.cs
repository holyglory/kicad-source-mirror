using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using KiCad.Automation.Mcp;
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
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
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
        int blockTransfer = 1;
        int artifactRequests = 0;
        app.MapGet("/artifacts/" + package.FileName, async (HttpContext context) =>
        {
            Interlocked.Increment(ref artifactRequests);
            if (Volatile.Read(ref blockTransfer) == 0)
            {
                await Results.File(catalogue.Files[package.FileName].Path, "application/octet-stream").ExecuteAsync(context);
                return;
            }
            context.Response.ContentLength = package.Bytes;
            await using var firstChunk = File.OpenRead(catalogue.Files[package.FileName].Path);
            byte[] buffer = new byte[4096];
            await firstChunk.ReadExactlyAsync(buffer, context.RequestAborted);
            await context.Response.Body.WriteAsync(buffer, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        try
        {
            await app.StartAsync(deadline.Token);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            string state = Directory.CreateDirectory(Path.Combine(temporary, "state")).FullName;
            string installedReceipt = Path.Combine(temporary, "installed-envelope.json");
            await File.WriteAllBytesAsync(installedReceipt, installed, deadline.Token);
            string configuration = Path.Combine(temporary, "updater.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new UpdatePreparationConfiguration(1,
                address + "/", Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo()), installedReceipt,
                state, temporary, "preview", "linux-x64", "tar.gz"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            string trustedCertificate = Path.Combine(temporary, "fixture-ca.pem");
            await File.WriteAllTextAsync(trustedCertificate, certificate.ExportCertificatePem(), deadline.Token);
            var start = UpdateCommandTests.StartInfo();
            start.ArgumentList.Add("--prepare-update");
            start.ArgumentList.Add("--configuration");
            start.ArgumentList.Add(configuration);
            // Trust this ephemeral certificate only in this disposable child.
            // Production HttpClient validation remains enabled; host trust is
            // neither changed nor bypassed with an accept-any callback.
            start.Environment["SSL_CERT_FILE"] = trustedCertificate;
            start.Environment["SSL_CERT_DIR"] = Directory.CreateDirectory(Path.Combine(temporary, "empty-ca-directory")).FullName;
            start.ArgumentList[1] = "--check-update";
            using (var checkProcess = Process.Start(start)!)
            {
                Task<string> checkOutput = checkProcess.StandardOutput.ReadToEndAsync(deadline.Token);
                Task<string> checkErrors = checkProcess.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await checkProcess.WaitForExitAsync(deadline.Token);
                    string lines = await checkOutput;
                    await File.WriteAllTextAsync(Path.Combine(evidence, "update-check.stdout.jsonl"), lines, deadline.Token);
                    Assert.AreEqual(0, checkProcess.ExitCode, await checkErrors);
                    using var checkedRelease = JsonDocument.Parse(lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
                    Assert.AreEqual("available", checkedRelease.RootElement.GetProperty("status").GetString());
                    Assert.AreEqual(package.Commit, checkedRelease.RootElement.GetProperty("commit").GetString());
                    Assert.AreEqual(0, Volatile.Read(ref artifactRequests), "Check-only mode must never download the artifact.");
                }
                finally
                {
                    if (!checkProcess.HasExited) checkProcess.Kill(entireProcessTree: true);
                    await checkProcess.WaitForExitAsync();
                    await Task.WhenAll(checkOutput, checkErrors);
                }
            }
            start.ArgumentList[1] = "--prepare-update";
            string standardOutput = "";
            foreach (bool cancelMidTransfer in new[] { true, false })
            {
                Volatile.Write(ref blockTransfer, cancelMidTransfer ? 1 : 0);
                using var process = Process.Start(start)!;
                bool interrupted = false;
                Task<string> stdout = ReadOutput();
                Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                string prefix = cancelMidTransfer ? "update-helper-cancelled" : "update-helper";
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                    standardOutput = await stdout;
                    await File.WriteAllTextAsync(Path.Combine(evidence, prefix + ".stdout.jsonl"), standardOutput, deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, prefix + ".stderr.log"), await stderr, deadline.Token);
                    Assert.AreEqual(cancelMidTransfer ? 2 : 0, process.ExitCode, "Inspect retained update-helper output.");
                    if (cancelMidTransfer)
                    {
                        Assert.IsTrue(interrupted, "The helper must be cancelled after a real transfer starts.");
                        using var cancelled = JsonDocument.Parse(standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
                        Assert.AreEqual("cancelled", cancelled.RootElement.GetProperty("status").GetString());
                        Assert.IsEmpty(Directory.GetFiles(temporary, "payload.partial", SearchOption.AllDirectories));
                        Assert.IsEmpty(Directory.GetFiles(temporary, "package.tar.gz", SearchOption.AllDirectories));
                        Assert.IsEmpty(Directory.GetDirectories(temporary, "stage-*"));
                    }
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                }

                async Task<string> ReadOutput()
                {
                    var lines = new List<string>();
                    while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                    {
                        lines.Add(line);
                        using var message = JsonDocument.Parse(line);
                        Assert.IsFalse(message.RootElement.GetProperty("installationReady").GetBoolean());
                        if (cancelMidTransfer && !interrupted && message.RootElement.GetProperty("status").GetString() == "downloading")
                        {
                            interrupted = true;
                            // Only the exact disposable child owned by this test.
                            Assert.AreEqual(0, SignalUpdateFixture(process.Id, 2));
                        }
                    }
                    return string.Join('\n', lines);
                }
            }
            using var terminal = JsonDocument.Parse(standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
            Assert.AreEqual("archive_staged", terminal.RootElement.GetProperty("status").GetString());
            Assert.IsFalse(terminal.RootElement.GetProperty("installationReady").GetBoolean());
            Assert.IsTrue(terminal.RootElement.GetProperty("nativeIdentityVerified").GetBoolean());
            Assert.AreEqual(package.Commit, terminal.RootElement.GetProperty("nativeCommit").GetString());
            string staged = terminal.RootElement.GetProperty("directory").GetString()!;
            string manifestDigest = terminal.RootElement.GetProperty("manifestSha256").GetString()!;
            File.Copy(Path.Combine(Path.GetDirectoryName(staged)!, "staging.json"), Path.Combine(evidence, "update-staging.json"));
            await File.WriteAllTextAsync(Path.Combine(evidence, "update-journey.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, package.Commit, package.Sha256, package.Bytes,
                manifestSha256 = manifestDigest, syntheticPublisher = true,
                publicFeed = false, activatedInstallation = false, qualifyingDelivery = false
            }, Evidence.JsonOptions), deadline.Token);
            await VerifyInstalledNative(Path.Combine(staged, "runtime"), evidence,
                Path.Combine(staged, "kicad-codex"), Path.Combine(staged, "kicad-mcp"));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(temporary, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SignalUpdateFixture(int processId, int signal);
}
