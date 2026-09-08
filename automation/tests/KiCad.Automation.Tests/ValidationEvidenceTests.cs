using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ValidationEvidenceTests
{
    private const string FixtureCommit = "1111111111111111111111111111111111111111";

    [TestMethod]
    public void ExactCommitAndCmakeLiteralDoNotAcceptAmbiguousInput()
    {
        foreach (string value in new[] { "main", "1111111", "--upload-pack=bad", new string('G', 40) })
            Assert.ThrowsExactly<ArgumentException>(() => Evidence.RequireCommit(value));
        Evidence.RequireCommit(FixtureCommit);
        Assert.AreEqual("[=[/a b/${literal}]=]", MacValidation.CmakeLiteral("/a b/${literal}"));
        Assert.AreEqual("[==[x]=]y]==]", MacValidation.CmakeLiteral("x]=]y"));
    }

    [TestMethod]
    public async Task LinuxCannotGenerateNativeMacExecutionEvidence()
    {
        if (OperatingSystem.IsMacOS()) return;
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => MacValidation.RunAsync(
            new("unused", FixtureCommit, "unused", "unused", "unused", ".", "arm64"), CancellationToken.None));
    }

    [TestMethod]
    public async Task SyntheticReceiptFixtureChecksIntegrityNotMacExecution()
    {
        // These explicitly synthetic fixtures test the receipt codec only.
        // They are not production evidence or a Mac execution receipt.
        string root = Directory.CreateTempSubdirectory("kicad-receipt-fixture-").FullName;
        try
        {
            string evidence = Directory.CreateDirectory(Path.Combine(root, "evidence")).FullName;
            string log = Path.Combine(evidence, "fixture.log");
            await File.WriteAllTextAsync(log, "Synthetic integrity-test fixture, not Mac execution.\n");
            var now = DateTimeOffset.UnixEpoch;
            var receipt = new ValidationReceipt(1, "macos", "fixture", "fixture", FixtureCommit, FixtureCommit,
                FixtureCommit, "synthetic", "synthetic", "synthetic", "synthetic", "checks_passed", null,
                [new("fixture", 0, now, now, "fixture.log", "fixture.log")],
                [new("fixture.log", new FileInfo(log).Length, Evidence.Hash(log))]);
            ValidationResult result = await Evidence.SealAsync(root, evidence, receipt);
            Assert.IsFalse(result.CrossPlatformReady);
            await Evidence.VerifyAsync(Path.Combine(root, "result.json"), result.Archive, FixtureCommit);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence.VerifyAsync(
                Path.Combine(root, "result.json"), result.Archive, FixtureCommit, architecture: "arm64"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence.VerifyAsync(
                Path.Combine(root, "result.json"), result.Archive, new string('2', 40)));
            await File.AppendAllTextAsync(result.Archive, "tamper");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence.VerifyAsync(
                Path.Combine(root, "result.json"), result.Archive, FixtureCommit));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedReceiptIsNeverAcceptedAsSuccessfulEvidence()
    {
        string root = Directory.CreateTempSubdirectory("kicad-receipt-fixture-").FullName;
        try
        {
            string evidence = Directory.CreateDirectory(Path.Combine(root, "evidence")).FullName;
            var receipt = new ValidationReceipt(1, "macos", "fixture", "fixture", FixtureCommit, FixtureCommit,
                null, null, "fixture", "fixture", "fixture", "failed", "Synthetic failure", [], []);
            ValidationResult result = await Evidence.SealAsync(root, evidence, receipt);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence.VerifyAsync(
                Path.Combine(root, "result.json"), result.Archive, FixtureCommit));
        }
        finally { Directory.Delete(root, true); }
    }
}
