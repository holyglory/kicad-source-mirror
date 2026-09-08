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
            string downloadedArchive = Directory.GetFiles(temporary, "package.tar.gz", SearchOption.AllDirectories).Single();
            string installedRoot = Path.Combine(temporary, "verified installation Énergie");
            // Cancel exactly at publication: authenticated/native-checked work
            // must not appear as an installed root until the directory commit.
            using (var stop = new CancellationTokenSource())
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => LinuxVerifiedInstallation.InstallAsync(
                    installedRoot, downloadedArchive, published, publisher.ExportSubjectPublicKeyInfo(),
                    new Uri(address + "/"), "preview", stop.Cancel, stop.Token));
                Assert.IsFalse(Directory.Exists(installedRoot));
                Assert.IsEmpty(Directory.GetDirectories(temporary, ".kicad-install-*"));
            }
            string conflictRoot = Path.Combine(temporary, "competing-installation");
            await Assert.ThrowsAsync<IOException>(() => LinuxVerifiedInstallation.InstallAsync(
                conflictRoot, downloadedArchive, published, publisher.ExportSubjectPublicKeyInfo(), new Uri(address + "/"), "preview",
                () =>
                {
                    Directory.CreateDirectory(conflictRoot);
                    File.WriteAllText(Path.Combine(conflictRoot, "existing.txt"), "Competing fixture must survive.");
                }, deadline.Token));
            Assert.AreEqual("Competing fixture must survive.", await File.ReadAllTextAsync(Path.Combine(conflictRoot, "existing.txt")));
            Assert.IsEmpty(Directory.GetDirectories(temporary, ".kicad-install-*"));

            string bootstrapKey = Path.Combine(temporary, "trusted-fixture-publisher.spki");
            string bootstrapEnvelope = Path.Combine(temporary, "bootstrap-envelope.json");
            string bootstrapRequest = Path.Combine(temporary, "bootstrap-request.json");
            await File.WriteAllBytesAsync(bootstrapKey, publisher.ExportSubjectPublicKeyInfo(), deadline.Token);
            await File.WriteAllBytesAsync(bootstrapEnvelope, published, deadline.Token);
            await File.WriteAllTextAsync(bootstrapRequest, JsonSerializer.Serialize(new LinuxInstallRequest(1,
                installedRoot, downloadedArchive, bootstrapEnvelope, address + "/", "preview"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            var bootstrapStart = UpdateCommandTests.StartInfo();
            foreach (string arg in new[] { "--install-package", "--configuration", bootstrapRequest, "--publisher-key", bootstrapKey })
                bootstrapStart.ArgumentList.Add(arg);
            InstalledLinuxUpdate firstInstall;
            using (var bootstrap = Process.Start(bootstrapStart)!)
            {
                Task<string> bootstrapOutput = bootstrap.StandardOutput.ReadToEndAsync(deadline.Token);
                Task<string> bootstrapErrors = bootstrap.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await bootstrap.WaitForExitAsync(deadline.Token);
                    string lines = await bootstrapOutput;
                    await File.WriteAllTextAsync(Path.Combine(evidence, "bootstrap.stdout.jsonl"), lines, deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "bootstrap.stderr.log"), await bootstrapErrors, deadline.Token);
                    Assert.AreEqual(0, bootstrap.ExitCode, "Inspect retained bootstrap output.");
                    using var result = JsonDocument.Parse(lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
                    Assert.AreEqual("installed", result.RootElement.GetProperty("status").GetString());
                    Assert.IsFalse(result.RootElement.GetProperty("automaticUpdatingQualified").GetBoolean());
                    firstInstall = result.RootElement.GetProperty("result").Deserialize<InstalledLinuxUpdate>(
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                }
                finally
                {
                    if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true);
                    await bootstrap.WaitForExitAsync();
                    await Task.WhenAll(bootstrapOutput, bootstrapErrors);
                }
            }
            var provisioned = JsonSerializer.Deserialize<UpdatePreparationConfiguration>(
                await File.ReadAllBytesAsync(firstInstall.ConfigurationPath, deadline.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.IsTrue(File.Exists(provisioned.InstalledEnvelope));
            Assert.IsTrue(Directory.Exists(provisioned.StateDirectory));
            Assert.IsTrue(Directory.Exists(provisioned.StagingDirectory));
            Assert.AreEqual(installedRoot, provisioned.InstallationRoot);
            Assert.AreEqual(manifestDigest, UpdateManifestCodec.Verify(await File.ReadAllBytesAsync(provisioned.InstalledEnvelope, deadline.Token),
                publisher.ExportSubjectPublicKeyInfo(), "preview").PayloadSha256);
            Assert.IsTrue(File.Exists(Path.Combine(firstInstall.ManagerDirectory, "current", "kicad-codex")));
            File.Copy(Path.Combine(installedRoot, "installed.json"), Path.Combine(evidence, "verified-installation.json"));
            await VerifyInstalledNative(Path.Combine(firstInstall.VersionDirectory, "runtime"), evidence,
                Path.Combine(firstInstall.ManagerDirectory, "current", "kicad-codex"),
                Path.Combine(firstInstall.ManagerDirectory, "current", "kicad-mcp"));
            string initialTarget = LinuxUpdateActivation.InspectTarget(firstInstall.ManagerDirectory);
            // A synthetic metadata revision of the same real archive proves
            // registration/isolation, not an application-version upgrade.
            byte[] nextEnvelope = UpdateManifestCodec.Sign(release with { Sequence = 3 }, publisher);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.RegisterAsync(
                installedRoot, "stale", downloadedArchive, nextEnvelope, deadline.Token));
            using (var wrongPublisher = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.RegisterAsync(
                    installedRoot, initialTarget, downloadedArchive, UpdateManifestCodec.Sign(release with { Sequence = 3 }, wrongPublisher), deadline.Token));
            published = nextEnvelope;
            var registrationStart = UpdateCommandTests.StartInfo();
            foreach (string arg in new[] { "--prepare-update", "--configuration", firstInstall.ConfigurationPath })
                registrationStart.ArgumentList.Add(arg);
            registrationStart.Environment["SSL_CERT_FILE"] = trustedCertificate;
            registrationStart.Environment["SSL_CERT_DIR"] = start.Environment["SSL_CERT_DIR"];
            RegisteredLinuxUpdate candidate;
            using (var registrationProcess = Process.Start(registrationStart)!)
            {
                Task<string> registrationOutput = registrationProcess.StandardOutput.ReadToEndAsync(deadline.Token);
                Task<string> registrationError = registrationProcess.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await registrationProcess.WaitForExitAsync(deadline.Token);
                    string lines = await registrationOutput;
                    await File.WriteAllTextAsync(Path.Combine(evidence, "registration.stdout.jsonl"), lines, deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "registration.stderr.log"), await registrationError, deadline.Token);
                    Assert.AreEqual(0, registrationProcess.ExitCode, "Inspect retained registration output.");
                    using var result = JsonDocument.Parse(lines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
                    var value = result.RootElement;
                    Assert.AreEqual("candidate_registered", value.GetProperty("status").GetString());
                    Assert.IsFalse(value.GetProperty("installationReady").GetBoolean());
                    Assert.IsFalse(value.GetProperty("reused").GetBoolean());
                    string candidatePath = value.GetProperty("directory").GetString()!;
                    candidate = new(new(installedRoot, candidatePath, firstInstall.ManagerDirectory,
                        Path.Combine(Path.GetDirectoryName(candidatePath)!, "update-config.json"),
                        value.GetProperty("manifestSha256").GetString()!, value.GetProperty("commit").GetString()!),
                        value.GetProperty("expectedTarget").GetString()!, false);
                }
                finally
                {
                    if (!registrationProcess.HasExited) registrationProcess.Kill(entireProcessTree: true);
                    await registrationProcess.WaitForExitAsync();
                    await Task.WhenAll(registrationOutput, registrationError);
                }
            }
            Assert.AreEqual(initialTarget, LinuxUpdateActivation.InspectTarget(firstInstall.ManagerDirectory));
            Assert.AreNotEqual(firstInstall.VersionDirectory, candidate.Version.VersionDirectory);
            DateTime modified = Directory.GetLastWriteTimeUtc(candidate.Version.VersionDirectory);
            var repeated = await LinuxVerifiedInstallation.RegisterAsync(installedRoot, initialTarget, downloadedArchive, nextEnvelope, deadline.Token);
            Assert.IsTrue(repeated.Reused);
            Assert.AreEqual(candidate.Version, repeated.Version);
            Assert.AreEqual(modified, Directory.GetLastWriteTimeUtc(candidate.Version.VersionDirectory));
            Assert.HasCount(2, Directory.GetDirectories(Path.Combine(installedRoot, "versions")));
            string acceptedPath = Path.Combine(installedRoot, "state", "accepted-envelope.json");
            await File.WriteAllBytesAsync(acceptedPath, UpdateManifestCodec.Sign(release with { Sequence = 4 }, publisher), deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.RegisterAsync(
                installedRoot, initialTarget, downloadedArchive, nextEnvelope, deadline.Token));
            await File.WriteAllBytesAsync(acceptedPath, nextEnvelope, deadline.Token);
            string candidateConfig = candidate.Version.ConfigurationPath;
            byte[] correctConfig = await File.ReadAllBytesAsync(candidateConfig, deadline.Token);
            var changedConfig = JsonSerializer.Deserialize<UpdatePreparationConfiguration>(correctConfig,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))! with { Origin = "https://different.example.test/" };
            await File.WriteAllTextAsync(candidateConfig, JsonSerializer.Serialize(changedConfig,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.RegisterAsync(
                installedRoot, initialTarget, downloadedArchive, nextEnvelope, deadline.Token));
            await File.WriteAllBytesAsync(candidateConfig, correctConfig, deadline.Token);
            string changedFile = Path.Combine(candidate.Version.VersionDirectory, "README.txt");
            byte[] correctReadme = await File.ReadAllBytesAsync(changedFile, deadline.Token);
            await File.AppendAllTextAsync(changedFile, "\nSynthetic candidate drift.\n", deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.RegisterAsync(
                installedRoot, initialTarget, downloadedArchive, nextEnvelope, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.ActivateAsync(
                installedRoot, initialTarget, candidate.Version.ManifestSha256, Guid.NewGuid(), deadline.Token));
            Assert.AreEqual(initialTarget, LinuxUpdateActivation.InspectTarget(firstInstall.ManagerDirectory));
            await File.WriteAllBytesAsync(changedFile, correctReadme, deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.ActivateAsync(
                installedRoot, "stale", candidate.Version.ManifestSha256, Guid.NewGuid(), deadline.Token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => LinuxVerifiedInstallation.ActivateAsync(
                installedRoot, initialTarget, "../arbitrary-directory", Guid.NewGuid(), deadline.Token));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => LinuxVerifiedInstallation.ActivateAsync(
                    installedRoot, initialTarget, candidate.Version.ManifestSha256, Guid.NewGuid(), cancelled.Token));
            }
            await File.WriteAllBytesAsync(acceptedPath, UpdateManifestCodec.Sign(release with { Sequence = 4 }, publisher), deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.ActivateAsync(
                installedRoot, initialTarget, candidate.Version.ManifestSha256, Guid.NewGuid(), deadline.Token));
            await File.WriteAllBytesAsync(acceptedPath, nextEnvelope, deadline.Token);
            Guid activateOperation = Guid.NewGuid();
            var activated = await LinuxVerifiedInstallation.ActivateAsync(installedRoot, initialTarget,
                candidate.Version.ManifestSha256, activateOperation, deadline.Token);
            Assert.AreEqual(activated.Activation.Target, LinuxUpdateActivation.InspectTarget(firstInstall.ManagerDirectory));
            Assert.AreEqual(candidate.Version, activated.SelectedVersion);
            Assert.AreEqual(activated, await LinuxVerifiedInstallation.ActivateAsync(installedRoot, initialTarget,
                candidate.Version.ManifestSha256, activateOperation, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.RollbackAsync(
                installedRoot, "wrong-target", activateOperation, Guid.NewGuid(), deadline.Token));
            // A broken candidate must not prevent recovery of the verified prior
            // version; the rollback does not execute or trust the broken bytes.
            await File.AppendAllTextAsync(changedFile, "\nSynthetic post-activation failure.\n", deadline.Token);
            byte[] checkpointBeforeRollback = await File.ReadAllBytesAsync(acceptedPath, deadline.Token);
            Guid rollbackOperation = Guid.NewGuid();
            var rolledBack = await LinuxVerifiedInstallation.RollbackAsync(installedRoot, activated.Activation.Target,
                activateOperation, rollbackOperation, deadline.Token);
            Assert.AreEqual(firstInstall, rolledBack.SelectedVersion);
            Assert.AreNotEqual(initialTarget, rolledBack.Activation.Target);
            Assert.AreEqual(rolledBack.Activation.Target, LinuxUpdateActivation.InspectTarget(firstInstall.ManagerDirectory));
            Assert.AreEqual(rolledBack, await LinuxVerifiedInstallation.RollbackAsync(installedRoot, activated.Activation.Target,
                activateOperation, rollbackOperation, deadline.Token));
            CollectionAssert.AreEqual(checkpointBeforeRollback, await File.ReadAllBytesAsync(acceptedPath, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxVerifiedInstallation.ActivateAsync(
                installedRoot, initialTarget, candidate.Version.ManifestSha256, activateOperation, deadline.Token));
            string recoveryEvidence = Directory.CreateDirectory(Path.Combine(evidence, "verified-rollback")).FullName;
            await VerifyInstalledNative(Path.Combine(firstInstall.VersionDirectory, "runtime"), recoveryEvidence,
                Path.Combine(firstInstall.ManagerDirectory, "current", "kicad-codex"),
                Path.Combine(firstInstall.ManagerDirectory, "current", "kicad-mcp"));
            await File.WriteAllTextAsync(Path.Combine(evidence, "candidate-registration.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, candidate.Version.ManifestSha256, candidate.Version.Commit,
                currentSelectionPreserved = true, unchangedCandidateReused = true, payloadDriftRejected = true,
                configurationDriftRejected = true, acceptedMetadataReplayRejected = true,
                verifiedSelectionSwitch = true, retainedPreviousVersionRestored = true, acceptedCheckpointPreserved = true,
                brokenCandidateRollback = true,
                syntheticMetadataRevision = true, applicationVersionUpgradeVerified = false, nativeEditorRestarted = false
            }, Evidence.JsonOptions), deadline.Token);
            await File.WriteAllBytesAsync(changedFile, correctReadme, deadline.Token);
            await VerifyUpdateRestartHandoff(firstInstall, candidate.Version,
                Directory.CreateDirectory(Path.Combine(evidence, "restart-failure-recovery")).FullName, deadline.Token,
                rejectCandidateStartup: true);
            await VerifyUpdateRestartHandoff(firstInstall, candidate.Version,
                Directory.CreateDirectory(Path.Combine(evidence, "restart-handoff")).FullName, deadline.Token);
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
