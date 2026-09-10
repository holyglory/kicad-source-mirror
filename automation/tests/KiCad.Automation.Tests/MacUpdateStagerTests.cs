using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacUpdateStagerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LinuxCannotSupplyNativeMacStagingEvidence()
    {
        if (OperatingSystem.IsMacOS()) { Assert.Inconclusive("This is the non-Mac refusal control."); return; }
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() =>
            MacUpdateStager.StageAsync(null!, null!, "/must-not-be-created", CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("NativeMac")]
    public async Task RealMacBundleStagesWithMetadataAndRunsItsOwnPackagedTools()
    {
        if (!OperatingSystem.IsMacOS())
        { Assert.Inconclusive("This requires native Mac extraction, signing checks and runtime execution."); return; }
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        Assert.IsTrue(arm || RuntimeInformation.ProcessArchitecture == Architecture.X64);
        string commit = arm ? "f79a41ed7aae781715877ef6c52ccf3762846b84" : "4c6a88502a4c84da7ff1cf4ae9ca54bd266a106d";
        string target = arm ? "osx-arm64" : "osx-x64";
        string hash = arm ? "aa2007dea343d87b5e8945ec22a5a91eee6f3b883ec06634dd93f2c4ea660ad6"
            : "5485b053ba9c18a2113e2e910285a5d7fdb7b73e940fbab12d1b5cd453d7f2b6";
        long size = arm ? 329413852 : 334286000;
        var artifact = new UpdateArtifact(target, "tar.gz", "kicad-codex-" + commit + "-macos-" + (arm ? "arm64" : "x64") + ".tar.gz", size, hash);
        string root = Directory.CreateTempSubdirectory("kicad-native-mac-stage-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "mac-update-stage")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); // Isolated test publisher; never the preview private key.
        var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "native-mac-staging-fixture", commit, [artifact]);
        var manifest = UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, key), key.ExportSubjectPublicKeyInfo(), "preview");
        try
        {
            string untouched = Path.Combine(root, "existing-user-work");
            await File.WriteAllTextAsync(untouched, "must remain", deadline.Token);
            using var downloads = new UpdateDownloader(new Uri("https://kicad.vr.ae/"));
            var download = await downloads.DownloadAsync(manifest, target, "tar.gz", root, cancellationToken: deadline.Token);
            var staged = await MacUpdateStager.StageAsync(manifest, download, root, deadline.Token);
            Assert.AreEqual(target, staged.Platform);
            Assert.AreEqual(manifest.PayloadSha256, staged.ManifestSha256);
            await NngBindingLifetimeTests.RunProbe(Path.Combine(staged.Directory, "managed/libnng.dylib"), deadline.Token);
            string receipt = Path.Combine(Path.GetDirectoryName(staged.Directory)!, "staging.json");
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, deadline.Token)))
            {
                Assert.IsFalse(json.RootElement.GetProperty("installationReady").GetBoolean());
                Assert.IsTrue(json.RootElement.GetProperty("nativeSignaturesVerified").GetBoolean());
                Assert.IsTrue(json.RootElement.GetProperty("nativeRuntimeVerified").GetBoolean());
                Assert.AreEqual(commit, json.RootElement.GetProperty("commit").GetString());
                Assert.IsTrue(json.RootElement.GetProperty("appleDoubleEntries").GetInt32() > 0);
            }
            File.Copy(receipt, Path.Combine(evidence, "staging.json"));
            foreach (string log in Directory.GetFiles(Path.GetDirectoryName(staged.Directory)!, "*.log"))
                File.Copy(log, Path.Combine(evidence, Path.GetFileName(log)));
            await Assert.ThrowsAsync<OperationCanceledException>(() => MacUpdateStager.StageAsync(manifest, download, root, new CancellationToken(true)));
            Assert.AreEqual("must remain", await File.ReadAllTextAsync(untouched, deadline.Token));

            // Correct archive bytes and signatures must still be rejected when
            // the declared native source is wrong. The earlier stage survives.
            var wrong = UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release with { Commit = new string('1', 40) }, key),
                key.ExportSubjectPublicKeyInfo(), "preview");
            var wrongDownload = new DownloadedUpdate(download.Path, artifact, wrong.PayloadSha256);
            await Assert.ThrowsExactlyAsync<IOException>(() => MacUpdateStager.StageAsync(wrong, wrongDownload, root, deadline.Token));
            Assert.IsTrue(File.Exists(Path.Combine(staged.Directory, "managed/kicad-mcp")));
            Assert.AreEqual("must remain", await File.ReadAllTextAsync(untouched, deadline.Token));
        }
        finally
        {
            // Retain failure diagnostics even when the first native stage fails.
            foreach (string directory in Directory.GetDirectories(root, "mac-update-diagnostics-*"))
            {
                string destination = Directory.CreateDirectory(Path.Combine(evidence, Path.GetFileName(directory))).FullName;
                foreach (string file in Directory.GetFiles(directory)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            foreach (string file in Directory.EnumerateFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            Directory.Delete(root, recursive: true);
        }
    }
}
