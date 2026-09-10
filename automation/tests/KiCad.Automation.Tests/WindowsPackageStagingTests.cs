using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsPackageStagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("WindowsArchivePayload")]
    public async Task InspectTheExactRealWindowsPackageWithoutExecutingIt()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var input = await Inputs(deadline.Token);
        await using var archive = File.OpenRead(input.Path);
        var plan = await WindowsArchivePreflight.InspectAsync(archive, deadline.Token);
        string evidence = Evidence();
        string path = Path.Combine(evidence, "archive-inspection.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, status = "passed", sourceCommit = input.Commit, archiveSha256 = input.Artifact.Sha256,
            archiveBytes = input.Artifact.Bytes, entries = plan.Entries.Count, expandedBytes = plan.ExpandedBytes,
            nativeWindowsExecution = false, installationReady = false
        }), deadline.Token);
        TestContext.AddResultFile(path);
    }

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsPackageStaging"), DoNotParallelize]
    public async Task StageAnActualKiCadArchiveAndRejectAWrongDeclaredCommit()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires actual Windows x64 package execution."); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var input = await Inputs(deadline.Token);
        string root = Directory.CreateTempSubdirectory("kwrealstage-").FullName;
        string evidence = Evidence();
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256); // Isolated test authority, not the preview publisher.
        try
        {
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "native-windows-staging-fixture", input.Commit, [input.Artifact]);
            var verified = Verify(release);
            var staged = await WindowsUpdateStager.StageAsync(verified, new(input.Path, input.Artifact, verified.PayloadSha256), root, deadline.Token);
            File.Copy(Path.Combine(Path.GetDirectoryName(staged.Directory)!, "staging.json"), Path.Combine(evidence, "staging.json"));
            foreach (string log in Directory.GetFiles(Path.GetDirectoryName(staged.Directory)!, "*.log"))
                File.Copy(log, Path.Combine(evidence, Path.GetFileName(log)));
            var wrong = Verify(release with { Commit = new string('0', 40) });
            await Assert.ThrowsExactlyAsync<IOException>(() => WindowsUpdateStager.StageAsync(wrong,
                new(input.Path, input.Artifact, wrong.PayloadSha256), root, deadline.Token));
            string failurePath = Directory.GetFiles(root, "failure.json", SearchOption.AllDirectories).Single();
            using (var failure = JsonDocument.Parse(await File.ReadAllTextAsync(failurePath, deadline.Token)))
                StringAssert.Contains(failure.RootElement.GetProperty("error").GetProperty("message").GetString()!,
                    "reports a different source commit");
            Assert.IsTrue(File.Exists(Path.Combine(staged.Directory, "bin/kicad.exe")));
            await File.WriteAllTextAsync(Path.Combine(evidence, "native-package-result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", sourceCommit = input.Commit, archiveSha256 = input.Artifact.Sha256,
                actualKiCadRuntimeVerified = true, syntheticExecutableFixture = false, isolatedTestPublisher = true,
                installationReady = false, nativeEditorJourneyVerified = false, automaticUpdatingQualified = false
            }), deadline.Token);

            VerifiedUpdateManifest Verify(UpdateRelease release) => UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, publisher),
                publisher.ExportSubjectPublicKeyInfo(), "preview");
        }
        finally
        {
            foreach (string directory in Directory.GetDirectories(root, "windows-update-diagnostics-*"))
            {
                string target = Directory.CreateDirectory(Path.Combine(evidence, Path.GetFileName(directory))).FullName;
                foreach (string file in Directory.GetFiles(directory)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
        }
    }

    internal static async Task<(string Path, string Commit, UpdateArtifact Artifact)> Inputs(CancellationToken token)
    {
        string path = Required("KICAD_TEST_WINDOWS_ARCHIVE"), commit = Required("KICAD_TEST_WINDOWS_ARCHIVE_COMMIT"),
            hash = Required("KICAD_TEST_WINDOWS_ARCHIVE_SHA256");
        Assert.IsTrue(Path.IsPathFullyQualified(path));
        Assert.IsTrue(commit.Length == 40 && commit.All(char.IsAsciiHexDigitLower));
        Assert.IsTrue(hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower));
        await using var file = File.OpenRead(path);
        Assert.IsTrue(file.Length is > 0 and <= UpdateManifestCodec.MaximumArtifactBytes);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        Assert.AreEqual(hash, actual, "The test input must be the exact retained native archive.");
        return (path, commit, new("win-x64", "zip", Path.GetFileName(path), file.Length, hash));
    }

    private string Evidence() => Directory.CreateDirectory(Path.Combine(
        Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!, "windows-real-package-staging")).FullName;
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " is required for exact Windows package evidence.");
}
