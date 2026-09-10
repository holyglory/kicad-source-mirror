using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacVerifiedInstallationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OtherPlatformsCannotCreateMacInstallations()
    {
        if (OperatingSystem.IsMacOS()) { Assert.Inconclusive("Non-Mac refusal check."); return; }
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => MacVerifiedInstallation.InstallAsync(
            "/must-not-be-created", "/no-archive", ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            new Uri("https://kicad.vr.ae/"), "preview"));
    }

    [TestMethod]
    [TestCategory("NativeMac")]
    public async Task NativeMacRegistrationActivationAndRollbackPreserveRetainedVersions()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Native Mac installation proof requires macOS."); return; }
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        string platform = arm ? "osx-arm64" : "osx-x64";
        string commit = arm ? "f79a41ed7aae781715877ef6c52ccf3762846b84" : "4c6a88502a4c84da7ff1cf4ae9ca54bd266a106d";
        var artifact = new UpdateArtifact(platform, "tar.gz", "kicad-codex-" + commit + "-macos-" + (arm ? "arm64" : "x64") + ".tar.gz",
            arm ? 329413852 : 334286000, arm ? "aa2007dea343d87b5e8945ec22a5a91eee6f3b883ec06634dd93f2c4ea660ad6"
                : "5485b053ba9c18a2113e2e910285a5d7fdb7b73e940fbab12d1b5cd453d7f2b6");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "mac-install-fixture-one", commit, [artifact]);
        byte[] firstEnvelope = UpdateManifestCodec.Sign(release, key), publicKey = key.ExportSubjectPublicKeyInfo();
        var first = UpdateManifestCodec.Verify(firstEnvelope, publicKey, "preview");
        var origin = new Uri("https://kicad.vr.ae/");
        string root = Directory.CreateTempSubdirectory("kicad-mac-registration-").FullName;
        string installedRoot = Path.Combine(root, "managed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            using var downloads = new UpdateDownloader(origin);
            var download = await downloads.DownloadAsync(first, platform, "tar.gz", root, cancellationToken: deadline.Token);
            string requestPath = Path.Combine(root, "install-request.json");
            string envelopePath = Path.Combine(root, "installed-envelope.json");
            string keyPath = Path.Combine(root, "trusted-publisher.spki");
            await File.WriteAllBytesAsync(envelopePath, firstEnvelope, deadline.Token);
            await File.WriteAllBytesAsync(keyPath, publicKey, deadline.Token);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new LinuxInstallRequest(1, installedRoot,
                download.Path, envelopePath, origin.AbsoluteUri, "preview"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            var command = UpdateCommandTests.StartInfo();
            command.WorkingDirectory = root;
            foreach (string argument in new[] { "--install-package", "--configuration", requestPath, "--publisher-key", keyPath })
                command.ArgumentList.Add(argument);
            using (var process = Process.Start(command)!)
            {
                Task<string> stdout = MacUpdateStager.ReadBounded(process.StandardOutput, deadline.Token);
                Task<string> stderr = MacUpdateStager.ReadBounded(process.StandardError, deadline.Token);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                    Assert.AreEqual(0, process.ExitCode, await stderr);
                    using var result = JsonDocument.Parse((await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
                    Assert.AreEqual("installed", result.RootElement.GetProperty("status").GetString());
                    Assert.IsFalse(result.RootElement.GetProperty("nativeEditorRestarted").GetBoolean());
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    try { await Task.WhenAll(stdout, stderr); }
                    catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { }
                }
            }
            string originalTarget = MacVerifiedInstallation.InspectTarget(installedRoot);
            var installed = await MacVerifiedInstallation.InspectSelectedAsync(installedRoot, originalTarget, deadline.Token);
            Assert.AreEqual(installed, await MacVerifiedInstallation.InspectSelectedAsync(installedRoot, originalTarget, deadline.Token));
            Assert.IsTrue(Directory.Exists(Path.Combine(installedRoot, "KiCad.app")));
            Assert.IsTrue(File.Exists(Path.Combine(installedRoot, "kicad-mcp")));
            await Assert.ThrowsAsync<IOException>(() => MacVerifiedInstallation.InstallAsync(installedRoot, download.Path,
                firstEnvelope, publicKey, origin, "preview", deadline.Token));

            byte[] nextEnvelope = UpdateManifestCodec.Sign(release with { Sequence = 2, Version = "mac-install-fixture-two" }, key);
            var next = UpdateManifestCodec.Verify(nextEnvelope, publicKey, "preview");
            var registered = await MacVerifiedInstallation.RegisterAsync(installedRoot, originalTarget, download.Path, nextEnvelope, deadline.Token);
            Assert.IsFalse(registered.Reused);
            Assert.AreEqual(originalTarget, MacVerifiedInstallation.InspectTarget(installedRoot));
            await File.WriteAllBytesAsync(Path.Combine(installedRoot, "state/accepted-envelope.json"), nextEnvelope, deadline.Token);
            await using var live = File.OpenRead(Path.Combine(installed.ApplicationBundle, "Contents/Info.plist"));
            Guid operation = Guid.NewGuid();
            var activation = await MacVerifiedInstallation.ActivateAsync(installedRoot, originalTarget, next.PayloadSha256, operation, deadline.Token);
            Assert.AreEqual(registered.Version, activation.SelectedVersion);
            Assert.AreEqual(activation, await MacVerifiedInstallation.ActivateAsync(installedRoot, originalTarget, next.PayloadSha256, operation, deadline.Token));
            Assert.AreEqual(installed, await MacVerifiedInstallation.InspectExecutableVersionAsync(installedRoot, installed.NativeExecutable, deadline.Token));
            Assert.IsTrue(live.ReadByte() >= 0, "Switching selection invalidated an already opened old-version file.");
            Guid rollbackId = Guid.NewGuid();
            var rollback = await MacVerifiedInstallation.RollbackAsync(installedRoot, activation.Activation.Target, operation, rollbackId, deadline.Token);
            Assert.AreEqual(installed, rollback.SelectedVersion);
            CollectionAssert.AreEqual(nextEnvelope, await File.ReadAllBytesAsync(Path.Combine(installedRoot, "state/accepted-envelope.json"), deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacVerifiedInstallation.ActivateAsync(installedRoot,
                originalTarget, next.PayloadSha256, operation, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacVerifiedInstallation.ActivateAsync(installedRoot,
                rollback.Activation.Target, first.PayloadSha256, Guid.NewGuid(), deadline.Token));
            var reused = await MacVerifiedInstallation.RegisterAsync(installedRoot, rollback.Activation.Target, download.Path, nextEnvelope, deadline.Token);
            Assert.IsTrue(reused.Reused);
            using (var held = new FileStream(Path.Combine(installedRoot, "registration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAsync<IOException>(() => MacVerifiedInstallation.RegisterAsync(installedRoot,
                    rollback.Activation.Target, download.Path, nextEnvelope, deadline.Token));
            string drift = Path.Combine(registered.Version.VersionDirectory, "unexpected-fixture-file");
            await File.WriteAllTextAsync(drift, "Synthetic drift", deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MacVerifiedInstallation.InspectActivationAsync(installedRoot,
                rollback.Activation.Target, next.PayloadSha256, deadline.Token));
            File.Delete(drift);
            _ = await MacVerifiedInstallation.InspectActivationAsync(installedRoot, rollback.Activation.Target, next.PayloadSha256, deadline.Token);
            await Assert.ThrowsAsync<OperationCanceledException>(() => MacVerifiedInstallation.RegisterAsync(installedRoot,
                rollback.Activation.Target, download.Path, nextEnvelope, new CancellationToken(true)));
            Assert.AreEqual(rollback.Activation.Target, MacVerifiedInstallation.InspectTarget(installedRoot));
            string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
                ?? TestContext.TestResultsDirectory!, "mac-registration")).FullName;
            string receipt = Path.Combine(evidence, "registration.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, platform, commit, installed = true, installCommandVerified = true,
                registered = true, selectionStayedDuringRegistration = true,
                activationVerified = true, retryVerified = true, rollbackVerified = true, liveVersionRetained = true,
                driftRejected = true, staleReplayRejected = true, nativeEditorsRestarted = false,
                automaticUpdatingQualified = false, fixturePublisher = true
            }), deadline.Token);
            TestContext.AddResultFile(receipt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
