using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsDiagnosticPackageTests
{
    [TestMethod]
    public async Task PreservesUnqualifiedBytesWithoutCreatingAPublicPackage()
    {
        string root = Directory.CreateTempSubdirectory("kwdiagnostic-").FullName;
        try
        {
            MakeSyntheticInstall(root);
            Assert.IsTrue(await WindowsDiagnosticPackage.RetainAsync(root, new string('a', 40), Prerequisites(), default));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "packages")));
            string evidence = Path.Combine(root, "diagnostics");
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "windows-diagnostic.json")));
            Assert.AreEqual("unqualified_diagnostic", receipt.RootElement.GetProperty("Status").GetString());
            Assert.IsFalse(receipt.RootElement.GetProperty("NativeEditorJourneyPassed").GetBoolean());
            Assert.IsFalse(receipt.RootElement.GetProperty("PubliclyPublishable").GetBoolean());
            string archive = Path.Combine(evidence, receipt.RootElement.GetProperty("Archive").GetString()!);
            byte[] before = await File.ReadAllBytesAsync(archive);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(before)), receipt.RootElement.GetProperty("Sha256").GetString());
            using var zip = ZipFile.OpenRead(archive);
            foreach (string name in new[] { "kicad.exe", "kicad-cli.exe", "kicad-mcp.exe", "nng.dll" })
            {
                using var reader = new StreamReader(zip.GetEntry("bin/" + name)!.Open());
                Assert.AreEqual("Synthetic archive fixture, not executable evidence.", await reader.ReadToEndAsync());
            }
            await Assert.ThrowsExactlyAsync<IOException>(() => WindowsDiagnosticPackage.RetainAsync(root, new string('a', 40), Prerequisites(), default));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(archive));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingNativeProofAndCancelledCaptureCannotProduceADiagnosticPackage()
    {
        string root = Directory.CreateTempSubdirectory("kwdiagnostic-").FullName;
        try
        {
            MakeSyntheticInstall(root);
            Assert.IsFalse(await WindowsDiagnosticPackage.RetainAsync(root, new string('a', 40), [], default));
            var failed = Prerequisites().Select(step => step.Name == "managed-runtime" ? step with { ExitCode = 1 } : step).ToArray();
            Assert.IsFalse(await WindowsDiagnosticPackage.RetainAsync(root, new string('a', 40), failed, default));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "diagnostics")));
            await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsDiagnosticPackage.RetainAsync(root, new string('a', 40), Prerequisites(), new CancellationToken(true)));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "diagnostics")));
            File.Delete(Path.Combine(root, "install/bin/nng.dll"));
            Assert.IsFalse(await WindowsDiagnosticPackage.RetainAsync(root, new string('a', 40), Prerequisites(), default));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void MakeSyntheticInstall(string root)
    {
        string bin = Directory.CreateDirectory(Path.Combine(root, "install/bin")).FullName;
        foreach (string name in new[] { "kicad.exe", "kicad-cli.exe", "kicad-mcp.exe", "nng.dll" })
            File.WriteAllText(Path.Combine(bin, name), "Synthetic archive fixture, not executable evidence.");
    }
    private static ValidationStep[] Prerequisites() => new[] { "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime" }
        .Select(name => new ValidationStep(name, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "synthetic.stdout", "synthetic.stderr")).ToArray();
}
