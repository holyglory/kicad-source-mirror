using System.Security.Cryptography;
using KiCad.Automation.Distribution;
using KiCad.Automation.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static readonly Lazy<string> CloseRecoveryEvidence = new(() =>
        NativeEvidenceDirectory.Begin(Path.Combine(FindRoot(), "automation/artifacts")));

    [TestMethod]
    [TestCategory("NativeUpdateCloseRecovery")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpdateChangingDuringSavePromptRestoresTheVerifiedPreviousEditor(bool concurrentSelection)
    {
        string cataloguePath = Environment.GetEnvironmentVariable("KICAD_PACKAGE_CATALOGUE")
            ?? throw new AssertFailedException("Select an exact frozen native package for recovery checks.");
        string? helperMode = Environment.GetEnvironmentVariable("KICAD_RECOVERY_PACKAGED_HELPER");
        Assert.IsTrue(helperMode is null or "0" or "1", "Select source (0) or packaged (1) helper explicitly.");
        var catalogue = await DownloadCatalogue.LoadAsync(cataloguePath);
        var artifact = catalogue.Manifest.Artifacts.Single(item => item.Platform == "linux-x64"
            && item.FileName.EndsWith(".tar.gz", StringComparison.Ordinal));
        string evidence = Directory.CreateDirectory(Path.Combine(CloseRecoveryEvidence.Value,
            concurrentSelection ? "selection-changed" : "candidate-changed")).FullName;
        string temporary = Directory.CreateTempSubdirectory("kicad-close-recovery-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = new UpdateRelease(1, "kicad-codex", "preview", 1, artifact.Version, artifact.Commit,
            [new("linux-x64", "tar.gz", artifact.FileName, artifact.Bytes, artifact.Sha256)]);
        try
        {
            // Both fixture metadata revisions bind the same real native archive
            // under a disposable publisher. Installation/registration need no
            // feed; a restored native helper may check the unavailable .invalid
            // origin without affecting the saved engineering document.
            var installed = await LinuxVerifiedInstallation.InstallAsync(Path.Combine(temporary, "installation"),
                catalogue.Files[artifact.FileName].Path, UpdateManifestCodec.Sign(first, publisher),
                publisher.ExportSubjectPublicKeyInfo(), new Uri("https://recovery.invalid/"), "preview", deadline.Token);
            byte[] candidateEnvelope = UpdateManifestCodec.Sign(first with { Sequence = 2 }, publisher);
            // Registration verifies an existing checkpoint but does not own its
            // advancement. Represent the completed authenticated background
            // check explicitly, before registering this fixture candidate.
            await File.WriteAllBytesAsync(Path.Combine(installed.Root, "state", "accepted-envelope.json"),
                candidateEnvelope, deadline.Token);
            var candidate = await LinuxVerifiedInstallation.RegisterAsync(installed.Root,
                LinuxUpdateActivation.InspectTarget(installed.ManagerDirectory), catalogue.Files[artifact.FileName].Path,
                candidateEnvelope, deadline.Token);
            await VerifyUpdateRestartHandoff(installed, candidate.Version, evidence, deadline.Token,
                rejectActivation: !concurrentSelection, changeSelectionDuringClose: concurrentSelection,
                useInstalledHelper: helperMode == "1");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
