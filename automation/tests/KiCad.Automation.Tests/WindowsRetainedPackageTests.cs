using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class WindowsRetainedPackageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsRetainedPackage")]
    public async Task OperateTheExactRetainedNativePackageWithoutRebuildingIt()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows execution."); return; }
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new AssertFailedException("Missing frozen input: " + name);
        string archive = Required("KICAD_RETAINED_WINDOWS_ARCHIVE"), expectedHash = Required("KICAD_RETAINED_WINDOWS_SHA256");
        string commit = Required("KICAD_RETAINED_WINDOWS_COMMIT"), originalReceipt = Required("KICAD_RETAINED_WINDOWS_RECEIPT");
        string scratch = Directory.CreateTempSubdirectory("kwretained-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-retained-package")).FullName;
        string? prior = Environment.GetEnvironmentVariable("KICAD_TEST_WINDOWS_INSTALL");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            byte[] receiptBytes = await File.ReadAllBytesAsync(originalReceipt, deadline.Token);
            using var receipt = JsonDocument.Parse(receiptBytes);
            Assert.AreEqual(commit, receipt.RootElement.GetProperty("SourceCommit").GetString());
            Assert.AreEqual("failed", receipt.RootElement.GetProperty("Status").GetString());
            foreach (string prerequisite in new[] { "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime" })
                Assert.IsTrue(receipt.RootElement.GetProperty("Steps").EnumerateArray().Any(step =>
                    step.GetProperty("Name").GetString() == prerequisite && step.GetProperty("ExitCode").GetInt32() == 0), prerequisite);
            await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, deadline.Token));
            Assert.AreEqual(expectedHash, hash);
            var declared = receipt.RootElement.GetProperty("DiagnosticArtifacts").EnumerateArray()
                .Single(item => item.GetProperty("Path").GetString() == Path.GetFileName(archive));
            Assert.AreEqual(hash, declared.GetProperty("Sha256").GetString());
            Assert.AreEqual(input.Length, declared.GetProperty("Bytes").GetInt64());
            input.Position = 0;
            var plan = await WindowsArchivePreflight.InspectAsync(input, deadline.Token);
            string install = Path.Combine(scratch, "install");
            input.Position = 0;
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true)) zip.ExtractToDirectory(install);
            foreach (var file in plan.Entries.Where(item => !item.Directory))
            {
                string path = Path.Combine(install, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Assert.AreEqual(file.Bytes, new FileInfo(path).Length);
                await using var bytes = File.OpenRead(path);
                Assert.AreEqual(file.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes, deadline.Token)), file.Path);
            }
            var nativeCommit = await WindowsLauncherTests.Invoke(Path.Combine(install, "bin/kicad-cli.exe"),
                ["version", "--format", "commit"], install, deadline.Token, input: null);
            Assert.AreEqual(0, nativeCommit.ExitCode, nativeCommit.Error);
            Assert.AreEqual(commit, nativeCommit.Output.Trim());
            await File.WriteAllTextAsync(Path.Combine(evidence, "inputs.json"), JsonSerializer.Serialize(new
            { sourceCommit = commit, archiveSha256 = hash, archiveBytes = input.Length, entries = plan.Entries.Count,
                originalRunId = receipt.RootElement.GetProperty("RunId").GetString(),
                harnessCommit = Environment.GetEnvironmentVariable("SOURCE_COMMIT"), rebuiltNativeCode = false }), deadline.Token);
            Environment.SetEnvironmentVariable("KICAD_TEST_WINDOWS_INSTALL", install);
            await new WindowsInstalledPackageTests { TestContext = TestContext }.PackagedMcpLaunchesRendersAndReattachesTwoDirtyNativeEditors();
            CollectionAssert.AreEqual(receiptBytes, await File.ReadAllBytesAsync(originalReceipt, deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, status = "passed", sourceCommit = commit, archiveSha256 = hash,
                exactRetainedPayload = true, nativeEditorJourneyPassed = true, rebuiltNativeCode = false,
                originalReceiptUnchanged = true, publiclyPublishable = false, crossPlatformReady = false }), deadline.Token);
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString()); throw; }
        finally
        {
            Environment.SetEnvironmentVariable("KICAD_TEST_WINDOWS_INSTALL", prior);
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
        }
    }
}
