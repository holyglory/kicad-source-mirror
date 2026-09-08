using System.Runtime.InteropServices;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacArchitectureTests
{
    [TestMethod]
    public void InstallPrefixCannotBecomeSeveralCmakeArguments()
    {
        MacValidation.RequireInstallOutputPath("/Users/operator/kicad-validation/arm64-1234");
        MacValidation.RequireInstallOutputPath("/Volumes/工程/build-x64");
        foreach (string path in new[] { "relative", "/Users/operator/My Build", "/tmp/one;two",
            "/tmp/line\nbreak", "/tmp/\"quoted\"", "/tmp/" + "$" + "{injected}", "/tmp/parent(child)",
            "/tmp/bracket[child]", "/tmp/comment#child", "/tmp/back\\slash" })
            Assert.ThrowsExactly<ArgumentException>(() => MacValidation.RequireInstallOutputPath(path));
    }

    [TestMethod]
    public void TargetCannotBeSubstitutedOrProvenByCrossCompilation()
    {
        Assert.AreEqual("arm64", MacArchitecture.CmakeName("arm64"));
        Assert.AreEqual("x86_64", MacArchitecture.CmakeName("x64"));
        Assert.ThrowsExactly<ArgumentException>(() => MacArchitecture.CmakeName("universal"));
        MacArchitecture.RequireExecutionTarget("arm64", Architecture.Arm64);
        MacArchitecture.RequireExecutionTarget("x64", Architecture.X64);
        Assert.ThrowsExactly<InvalidDataException>(() => MacArchitecture.RequireExecutionTarget("arm64", Architecture.X64));
        Assert.ThrowsExactly<InvalidDataException>(() => MacArchitecture.RequireExecutionTarget("x64", Architecture.Arm64));
        MacArchitecture.RequireBinaryTarget("arm64", "arm64");
        MacArchitecture.RequireBinaryTarget("x64", "x86_64");
        MacArchitecture.RequireBinaryTarget("arm64", "x86_64 arm64\n");
        MacArchitecture.RequireBinaryTarget("x64", "x86_64 arm64");
        foreach (string invalid in new[] { null!, "", "x86_64", "arm64 arm64", "arm64 unknown" })
            Assert.ThrowsExactly<InvalidDataException>(() => MacArchitecture.RequireBinaryTarget("arm64", invalid));
    }

    [TestMethod]
    public async Task SyntheticReceiptIntegrityDoesNotConflateTheTwoMacTargets()
    {
        // Synthetic codec fixture only. No native Mac execution or artifact is
        // created here, and checksum verification remains distinct from proof.
        string root = Directory.CreateTempSubdirectory("kicad-mac-architecture-fixture-").FullName;
        try
        {
            const string commit = "1111111111111111111111111111111111111111";
            string evidence = Directory.CreateDirectory(Path.Combine(root, "evidence")).FullName;
            string log = Path.Combine(evidence, "synthetic.log");
            await File.WriteAllTextAsync(log, "Synthetic architecture-claim integrity fixture.");
            var receipt = new ValidationReceipt(2, "macos", "Arm64", "Arm64", commit, commit,
                commit, "", "synthetic", "synthetic", "synthetic", "checks_passed", null,
                [new("synthetic", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "synthetic.log", "synthetic.log")],
                [new("synthetic.log", new FileInfo(log).Length, Evidence.Hash(log))], "arm64",
                [new("kicad", "arm64", new string('2', 64)), new("kicad-cli", "arm64", new string('3', 64))]);
            var result = await Evidence.SealAsync(root, evidence, receipt);
            Assert.IsFalse(result.CrossPlatformReady);
            await Evidence.VerifyAsync(Path.Combine(root, "result.json"), result.Archive, commit, architecture: "arm64");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                Evidence.VerifyAsync(Path.Combine(root, "result.json"), result.Archive, commit, architecture: "x64"));
            var invalidReceipts = new[]
            {
                receipt with { ProcessArchitecture = "X64" },
                receipt with { ProcessArchitecture = "unknown" },
                receipt with { NativeBinaries = null },
                receipt with { NativeBinaries = [new("kicad", "arm64", new string('2', 64))] },
                receipt with { NativeBinaries = [new("kicad", "x86_64", new string('2', 64)),
                    new("kicad-cli", "arm64", new string('3', 64))] }
            };
            for (int index = 0; index < invalidReceipts.Length; index++)
            {
                string variant = Directory.CreateDirectory(Path.Combine(root, "invalid-" + index)).FullName;
                string variantEvidence = Directory.CreateDirectory(Path.Combine(variant, "evidence")).FullName;
                File.Copy(log, Path.Combine(variantEvidence, "synthetic.log"));
                var invalid = await Evidence.SealAsync(variant, variantEvidence, invalidReceipts[index]);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Evidence.VerifyAsync(
                    Path.Combine(variant, "result.json"), invalid.Archive, commit, architecture: "arm64"));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
