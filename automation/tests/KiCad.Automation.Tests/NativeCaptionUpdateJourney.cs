using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
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
    private static readonly Lazy<string> CaptionUpdateEvidence = new(() =>
        NativeEvidenceDirectory.Begin(Path.Combine(FindRoot(), "automation/artifacts")));

    [TestMethod]
    [TestCategory("NativeCaptionUpdate")]
    [DataRow(false)]
    [DataRow(true)]
    public Task InstalledCaptionUpdateRejectsDriftCancelsAndRestartsAfterSave(bool automaticContext)
        => RunCaptionUpdateJourney(automaticContext, publicChannel: false);

    [TestMethod]
    [TestCategory("PublicCaptionUpdate")]
    [TestCategory("ExternalIntegration")]
    public Task PublicSignedCaptionUpdateDownloadsPreservesDirtyWorkAndRestartsTwoProjects()
        => RunCaptionUpdateJourney(automaticContext: true, publicChannel: true);

    [TestMethod]
    [TestCategory("EmptyCaptionUpdate")]
    public Task EmptyManagerCaptionUpdateRejectsDriftAndRestartsWithoutOpeningAProject()
        => RunCaptionUpdateJourney(automaticContext: true, publicChannel: false, emptyManager: true);

    private async Task RunCaptionUpdateJourney(bool automaticContext, bool publicChannel, bool emptyManager = false)
    {
        string cataloguePath = Environment.GetEnvironmentVariable("KICAD_CAPTION_PACKAGE_CATALOGUE")
            ?? throw new AssertFailedException("Select the frozen caption-enabled package catalogue.");
        string candidatePath = Environment.GetEnvironmentVariable("KICAD_CAPTION_CANDIDATE_CATALOGUE")
            ?? throw new AssertFailedException("Select the exact candidate package catalogue.");
        var catalogue = await DownloadCatalogue.LoadAsync(automaticContext && !publicChannel ? candidatePath : cataloguePath);
        var candidateCatalogue = await DownloadCatalogue.LoadAsync(candidatePath);
        var artifact = catalogue.Manifest.Artifacts.Single(item => item.Platform == "linux-x64" && item.FileName.EndsWith(".tar.gz", StringComparison.Ordinal));
        var candidateArtifact = candidateCatalogue.Manifest.Artifacts.Single(item => item.Platform == "linux-x64" && item.FileName.EndsWith(".tar.gz", StringComparison.Ordinal));
        if (!automaticContext || publicChannel) Assert.AreNotEqual(artifact.Commit, candidateArtifact.Commit, "This case must upgrade between different source builds.");
        string evidence = Directory.CreateDirectory(Path.Combine(CaptionUpdateEvidence.Value,
            emptyManager ? "empty-manager" : publicChannel ? "public-signed" : automaticContext ? "automatic-context" : "different-build")).FullName;
        string temporary = Directory.CreateTempSubdirectory("kicad-caption-ui-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        WebApplication? app = null;
        X509Certificate2? certificate = null;
        var processes = new List<Process>();
        var captures = new List<Task>();
        string? displayName = null;
        string installationRoot = Path.Combine(temporary, "installation");
        try
        {
            byte[] installedEnvelope, candidateEnvelope, publisherKey;
            string origin;
            if (publicChannel)
            {
                origin = Environment.GetEnvironmentVariable("KICAD_PUBLIC_DOWNLOAD_URL")
                    ?? throw new AssertFailedException("Select the real public feed.");
                publisherKey = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("KICAD_UPDATE_PUBLISHER_SPKI_FILE")
                    ?? throw new AssertFailedException("Select the trusted public key."), deadline.Token);
                installedEnvelope = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("KICAD_CAPTION_INSTALLED_ENVELOPE")
                    ?? throw new AssertFailedException("Select the authentic previous release envelope."), deadline.Token);
                using var source = new UpdateDownloader(new Uri(origin));
                candidateEnvelope = await source.FetchManifestAsync("preview", deadline.Token);
            }
            else
            {
                using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                publisherKey = publisher.ExportSubjectPublicKeyInfo();
                installedEnvelope = UpdateManifestCodec.Sign(new(1, "kicad-codex", "preview", 1, artifact.Version, artifact.Commit,
                    [new("linux-x64", "tar.gz", artifact.FileName, artifact.Bytes, artifact.Sha256)]), publisher);
                candidateEnvelope = UpdateManifestCodec.Sign(new(1, "kicad-codex", "preview", 2,
                    candidateArtifact.Version, candidateArtifact.Commit, [new("linux-x64", "tar.gz", candidateArtifact.FileName,
                        candidateArtifact.Bytes, candidateArtifact.Sha256)]), publisher);
                using var tls = RSA.Create(2048);
                var certificateRequest = new CertificateRequest("CN=localhost", tls, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddIpAddress(IPAddress.Loopback);
                certificateRequest.CertificateExtensions.Add(names.Build());
                certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                app = builder.Build();
                app.MapGet("/updates/preview.json", () => Results.Bytes(candidateEnvelope, "application/json"));
                app.MapGet("/artifacts/" + candidateArtifact.FileName, () => Results.File(candidateCatalogue.Files[candidateArtifact.FileName].Path, "application/octet-stream"));
                await app.StartAsync(deadline.Token);
                origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
            }
            var previousManifest = UpdateManifestCodec.Verify(installedEnvelope, publisherKey, "preview");
            var candidateManifest = UpdateManifestCodec.Verify(candidateEnvelope, publisherKey, "preview",
                new(previousManifest.Release.Sequence, previousManifest.PayloadSha256));
            Assert.AreEqual(artifact.Commit, previousManifest.Release.Commit);
            Assert.AreEqual(artifact.Sha256, previousManifest.ForInstallation("linux-x64", "tar.gz")!.Sha256);
            Assert.AreEqual(candidateArtifact.Commit, candidateManifest.Release.Commit);
            Assert.AreEqual(candidateArtifact.Sha256, candidateManifest.ForInstallation("linux-x64", "tar.gz")!.Sha256);
            Assert.IsGreaterThan(previousManifest.Release.Sequence, candidateManifest.Release.Sequence);
            var installed = await LinuxVerifiedInstallation.InstallAsync(installationRoot, catalogue.Files[artifact.FileName].Path,
                installedEnvelope, publisherKey, new Uri(origin), "preview", deadline.Token);
            string initialTarget = LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory);
            string candidateDirectory = Path.Combine(installationRoot, "versions", candidateManifest.PayloadSha256, "payload");
            if (publicChannel) Assert.IsFalse(Directory.Exists(candidateDirectory), "Public journey must download/register through the running native helper.");
            else await LinuxVerifiedInstallation.RegisterAsync(installationRoot, initialTarget,
                candidateCatalogue.Files[candidateArtifact.FileName].Path, candidateEnvelope, deadline.Token);
            string engineering = Directory.CreateDirectory(Path.Combine(temporary, "project")).FullName;
            string project = Path.Combine(engineering, "fixture.kicad_pro");
            string schematic = Path.Combine(engineering, "fixture.kicad_sch");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
            {
                meta = new { version = 3 }, schematic = new { top_level_sheets = new[]
                { new { uuid = Guid.NewGuid().ToString("D"), name = "fixture", filename = "fixture.kicad_sch" } } }
            }), deadline.Token);
            byte[] projectBefore = await File.ReadAllBytesAsync(project, deadline.Token);
            string certificateFile = Path.Combine(temporary, "test-ca.pem");
            if (certificate is not null)
                await File.WriteAllTextAsync(certificateFile, certificate.ExportCertificatePem(), deadline.Token);
            var displayStart = new ProcessStartInfo("Xvfb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" }) displayStart.ArgumentList.Add(arg);
            Process display = Process.Start(displayStart)!;
            processes.Add(display);
            captures.Add(Capture(display.StandardError, Path.Combine(evidence, "caption-xvfb.log")));
            displayName = ":" + await display.StandardOutput.ReadLineAsync(deadline.Token);
            string nativeLog = Path.Combine(evidence, "caption-native.log");
            string instance = Guid.NewGuid().ToString("D");
            string socket = Path.Combine(temporary, "old.sock");
            string prefix = Path.Combine(installed.VersionDirectory, "runtime");
            var start = new ProcessStartInfo(Path.Combine(installed.VersionDirectory, "kicad-codex"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = engineering };
            start.Environment["DISPLAY"] = displayName;
            start.Environment["WXTRACE"] = "KICAD_AUTOMATION_UPDATES";
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(temporary, "profile");
            start.Environment["KICAD_CACHE_HOME"] = Path.Combine(temporary, "cache");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(temporary, "xdg-profile");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(temporary, "xdg-cache");
            start.Environment.Remove("KICAD_AUTOMATION_UPDATE_HELPER");
            start.Environment.Remove("KICAD_AUTOMATION_UPDATE_CONFIG");
            if (!automaticContext)
            {
                start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = Path.Combine(prefix, "lib/kicad-automation/kicad-mcp");
                start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = installed.ConfigurationPath;
            }
            if (!publicChannel)
            {
                start.Environment["SSL_CERT_FILE"] = certificateFile;
                start.Environment["SSL_CERT_DIR"] = Directory.CreateDirectory(Path.Combine(temporary, "empty-certificates")).FullName;
            }
            foreach (string arg in new[] { "--new", emptyManager ? "--update-manager" : "--automation", instance,
                "--api-socket", socket, "--automation-log", nativeLog, "--software-rendering" }) start.ArgumentList.Add(arg);
            if (!emptyManager) start.ArgumentList.Add(project);
            Process native = Process.Start(start)!;
            processes.Add(native);
            captures.Add(Capture(native.StandardOutput, Path.Combine(evidence, "caption.stdout.log")));
            captures.Add(Capture(native.StandardError, Path.Combine(evidence, "caption.stderr.log")));
            var client = new NativeClient(new NngTransport(), "ipc://" + socket);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(native.HasExited);
                try { await client.HandshakeAsync(deadline.Token); break; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, deadline.Token);
            }
            if (!emptyManager) await client.CreateRootSchematicAsync(schematic, deadline.Token);
            else Assert.AreEqual("", (await client.HandshakeAsync(deadline.Token)).ProjectPath);
            await WaitLog("\"status\":\"candidate_available\"");
            Assert.IsTrue(Directory.Exists(candidateDirectory));
            Process? secondNative = null;
            NativeClient? secondClient = null;
            string? secondSchematic = null;
            string? secondEpoch = null;
            string? secondInstance = null;
            if (automaticContext && !emptyManager)
            {
                string secondDirectory = Directory.CreateDirectory(Path.Combine(temporary, "second-project")).FullName;
                string secondProject = Path.Combine(secondDirectory, "second.kicad_pro");
                secondSchematic = Path.Combine(secondDirectory, "second.kicad_sch");
                await File.WriteAllTextAsync(secondProject, JsonSerializer.Serialize(new
                {
                    meta = new { version = 3 }, schematic = new { top_level_sheets = new[]
                    { new { uuid = Guid.NewGuid().ToString("D"), name = "second", filename = "second.kicad_sch" } } }
                }), deadline.Token);
                string secondSocket = Path.Combine(temporary, "second.sock");
                string secondLog = Path.Combine(evidence, "second-native.log");
                secondInstance = Guid.NewGuid().ToString("D");
                var secondStart = new ProcessStartInfo(start.FileName)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = secondDirectory };
                foreach (var (name, value) in start.Environment) secondStart.Environment[name] = value;
                secondStart.Environment["KICAD_CONFIG_HOME"] = Path.Combine(temporary, "second-profile");
                secondStart.Environment["KICAD_CACHE_HOME"] = Path.Combine(temporary, "second-cache");
                foreach (string arg in new[] { "--new", "--automation", secondInstance, "--api-socket", secondSocket,
                    "--automation-log", secondLog, "--software-rendering", secondProject }) secondStart.ArgumentList.Add(arg);
                secondNative = Process.Start(secondStart)!;
                processes.Add(secondNative);
                captures.Add(Capture(secondNative.StandardOutput, Path.Combine(evidence, "second.stdout.log")));
                captures.Add(Capture(secondNative.StandardError, Path.Combine(evidence, "second.stderr.log")));
                secondClient = new NativeClient(new NngTransport(), "ipc://" + secondSocket);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Assert.IsFalse(secondNative.HasExited);
                    try { await secondClient.HandshakeAsync(deadline.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(100, deadline.Token);
                }
                secondEpoch = secondClient.Epoch;
                await secondClient.CreateRootSchematicAsync(secondSchematic, deadline.Token);
                while (!File.Exists(secondLog) || !(await File.ReadAllTextAsync(secondLog, deadline.Token)).Contains("\"status\":\"candidate_available\"", StringComparison.Ordinal))
                    await Task.Delay(100, deadline.Token);
            }
            NativeKeyboard.SchematicShortcut(displayName, native.Id, "motion", "KiCad", false, true,
                clickFromRight: 50, clickFromTop: 100);
            await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "update-available.png"), deadline.Token);
            string changedFile = Path.Combine(candidateDirectory, "README.txt");
            byte[] original = await File.ReadAllBytesAsync(changedFile, deadline.Token);
            await File.AppendAllTextAsync(changedFile, "Synthetic caption preflight drift.", deadline.Token);
            ClickUpdate();
            await WaitWindow("Error");
            await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "update-rejected.png"), deadline.Token);
            Assert.IsFalse(native.HasExited);
            Assert.AreEqual(initialTarget, LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory));
            NativeKeyboard.SchematicShortcut(displayName, native.Id, "Return", "Error", false, false);
            await File.WriteAllBytesAsync(changedFile, original, deadline.Token);
            byte[] saved = [];
            if (!emptyManager)
            {
                ClickUpdate();
                await WaitWindow("Save");
                await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "update-save-prompt.png"), deadline.Token);
                NativeKeyboard.SchematicShortcut(displayName, native.Id, "Escape", "Save", false, false);
                await WaitLog("\"status\":\"cancelled\"");
                Assert.IsFalse(native.HasExited);
                Assert.AreEqual(initialTarget, LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory));
                Assert.IsFalse(File.Exists(schematic));
                NativeKeyboard.SchematicShortcut(displayName, native.Id, "s");
                while (!File.Exists(schematic)) await Task.Delay(100, deadline.Token);
                saved = await File.ReadAllBytesAsync(schematic, deadline.Token);
            }
            ClickUpdate();
            await native.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, native.ExitCode);
            string handovers = Path.Combine(installationRoot, "handovers");
            UpdateHandoffState? restarted = null;
            while (restarted is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                foreach (string directory in Directory.GetDirectories(handovers))
                {
                    string statePath = Path.Combine(directory, "state.json");
                    if (!File.Exists(statePath)) continue;
                    var state = JsonSerializer.Deserialize<UpdateHandoffState>(await File.ReadAllBytesAsync(statePath, deadline.Token),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    if (state.Status == "restarted") restarted = state;
                    Assert.IsFalse(state.Status is "activation_failed" or "reconciliation_required" or "launch_failed", state.Error);
                }
                if (restarted is null) await Task.Delay(100, deadline.Token);
            }
            Process replacement = Process.GetProcessById(restarted.ProcessId!.Value);
            processes.Add(replacement);
            Assert.AreEqual(restarted.ProcessIdentity, LinuxProcessIdentity.Read(replacement.Id));
            Assert.AreEqual(Path.Combine(candidateDirectory, "runtime/bin/kicad"), replacement.MainModule!.FileName);
            var peer = new NativeClient(new NngTransport(), restarted.Endpoint!, restarted.NativeEpoch);
            var session = await peer.HandshakeAsync(deadline.Token);
            Assert.AreEqual(instance, session.InstanceId);
            Assert.AreNotEqual(client.Epoch, session.Epoch);
            Assert.AreEqual(emptyManager ? "" : project, session.ProjectPath);
            if (!emptyManager)
            {
                await peer.OpenRootSchematicAsync(schematic, deadline.Token);
                CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(schematic, deadline.Token));
            }
            else
            {
                Assert.IsFalse(File.Exists(schematic));
                CollectionAssert.AreEqual(projectBefore, await File.ReadAllBytesAsync(project, deadline.Token));
            }
            await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "after-caption-update.png"), deadline.Token);
            if (secondNative is not null)
            {
                Assert.IsFalse(secondNative.HasExited, "Updating one project must not close the other editor.");
                Assert.AreEqual(secondEpoch, (await secondClient!.HandshakeAsync(deadline.Token)).Epoch);
                NativeKeyboard.SchematicShortcut(displayName, secondNative.Id, "s");
                while (!File.Exists(secondSchematic)) await Task.Delay(100, deadline.Token);
                byte[] secondSaved = await File.ReadAllBytesAsync(secondSchematic!, deadline.Token);
                string[] existingHandovers = Directory.GetDirectories(handovers);
                NativeKeyboard.SchematicShortcut(displayName, secondNative.Id, "click", "KiCad", false, true,
                    clickFromRight: 170, clickFromTop: 25);
                await secondNative.WaitForExitAsync(deadline.Token);
                UpdateHandoffState? secondRestart = null;
                while (secondRestart is null)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    foreach (string directory in Directory.GetDirectories(handovers).Except(existingHandovers))
                    {
                        string path = Path.Combine(directory, "state.json");
                        if (!File.Exists(path)) continue;
                        var state = JsonSerializer.Deserialize<UpdateHandoffState>(await File.ReadAllBytesAsync(path, deadline.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                        if (state.Status == "restarted") secondRestart = state;
                        Assert.IsFalse(state.Status is "activation_failed" or "reconciliation_required" or "launch_failed", state.Error);
                    }
                    if (secondRestart is null) await Task.Delay(100, deadline.Token);
                }
                Process secondReplacement = Process.GetProcessById(secondRestart.ProcessId!.Value);
                processes.Add(secondReplacement);
                Assert.AreEqual(secondRestart.ProcessIdentity, LinuxProcessIdentity.Read(secondReplacement.Id));
                var secondPeer = new NativeClient(new NngTransport(), secondRestart.Endpoint!, secondRestart.NativeEpoch);
                Assert.AreEqual(secondInstance, (await secondPeer.HandshakeAsync(deadline.Token)).InstanceId);
                Assert.AreNotEqual(secondEpoch, secondPeer.Epoch);
                await secondPeer.OpenRootSchematicAsync(secondSchematic!, deadline.Token);
                CollectionAssert.AreEqual(secondSaved, await File.ReadAllBytesAsync(secondSchematic!, deadline.Token));
                await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "second-instance-updated.png"), deadline.Token);
                Assert.IsFalse(replacement.HasExited);
                NativeKeyboard.SchematicShortcut(displayName, secondReplacement.Id, "q", "KiCad", true, false);
                await secondReplacement.WaitForExitAsync(deadline.Token);
            }
            NativeKeyboard.SchematicShortcut(displayName, replacement.Id, "q", "KiCad", true, false);
            await replacement.WaitForExitAsync(deadline.Token);
            foreach (string directory in Directory.GetDirectories(handovers))
            {
                string copy = Directory.CreateDirectory(Path.Combine(evidence, "handovers", Path.GetFileName(directory))).FullName;
                foreach (string file in Directory.GetFiles(directory)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "caption-update-result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, currentCommit = artifact.Commit, candidateCommit = candidateArtifact.Commit,
                currentArchiveSha256 = artifact.Sha256, candidateArchiveSha256 = candidateArtifact.Sha256,
                captionClickVerified = true, driftRejectedBeforeClose = true, dirtyCloseCancelled = !emptyManager,
                saveAndRestartVerified = !emptyManager, restartVerified = true, nativeEpochChanged = true,
                emptyManagerPreserved = emptyManager,
                differentBuildUpgrade = artifact.Commit != candidateArtifact.Commit,
                automaticManagedContext = automaticContext, secondLiveInstanceCaptionUpdate = secondNative is not null,
                productionSigning = publicChannel, publicDownloadAndRegistration = publicChannel,
                publisherKeySha256 = candidateManifest.PublisherKeySha256, origin,
                automaticUpdatingQualified = false, nativeMacVerified = false
            }), deadline.Token);

            void ClickUpdate() => NativeKeyboard.SchematicShortcut(displayName, native.Id, "click", "KiCad", false, true,
                clickFromRight: 170, clickFromTop: 25);
            async Task WaitLog(string value)
            {
                while (!File.Exists(nativeLog) || !(await File.ReadAllTextAsync(nativeLog, deadline.Token)).Contains(value, StringComparison.Ordinal))
                {
                    Assert.IsFalse(native.HasExited);
                    await Task.Delay(100, deadline.Token);
                }
            }
            async Task WaitWindow(string title)
            {
                using var shortDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                shortDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                while (!NativeKeyboard.HasWindow(displayName, native.Id, title)) await Task.Delay(100, shortDeadline.Token);
            }
        }
        finally
        {
            if (displayName is not null)
            {
                try { await NativeKeyboard.CaptureAsync(displayName, Path.Combine(evidence, "final-caption-display.png"), CancellationToken.None); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
            }
            string handovers = Path.Combine(installationRoot, "handovers");
            if (Directory.Exists(handovers))
                foreach (string directory in Directory.GetDirectories(handovers))
                {
                    string path = Path.Combine(directory, "state.json");
                    if (!File.Exists(path)) continue;
                    var state = JsonSerializer.Deserialize<UpdateHandoffState>(await File.ReadAllBytesAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (state?.ProcessIdentity is not { } identity || processes.Any(process => process.Id == identity.ProcessId)) continue;
                    try
                    {
                        Process owned = Process.GetProcessById(identity.ProcessId);
                        if (!owned.HasExited && LinuxProcessIdentity.Read(owned.Id) == identity) processes.Add(owned);
                        else owned.Dispose();
                    }
                    catch (ArgumentException) { }
                }
            foreach (Process process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await Task.WhenAll(captures);
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            certificate?.Dispose();
            Directory.Delete(temporary, recursive: true);
        }
    }
}
