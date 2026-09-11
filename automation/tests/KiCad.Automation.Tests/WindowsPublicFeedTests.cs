using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass, DoNotParallelize]
public sealed class WindowsPublicFeedTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("NativeWindowsPublicFeed")]
    public async Task TheInstalledWindowsHelperChecksItsPublicFeedWithoutChangingItsVersion()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the real Windows package and native execution."); return; }
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new AssertFailedException("Missing frozen input: " + name);
        var frozen = WindowsRetainedPackageInput.Parse(Required("KICAD_RETAINED_WINDOWS_INPUT"));
        byte[] key = await File.ReadAllBytesAsync(Required("KICAD_WINDOWS_PUBLIC_PUBLISHER"));
        var origin = new Uri("https://kicad.vr.ae/platforms/win-x64/");
        string scratch = Directory.CreateTempSubdirectory("kwpublic-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Required("KICAD_HOSTED_FIXTURE_EVIDENCE"), "windows-public-feed")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var source = new UpdateDownloader(origin);
        try
        {
            byte[] envelope = await source.FetchManifestAsync("preview", deadline.Token);
            var manifest = UpdateManifestCodec.Verify(envelope, key, "preview");
            Assert.AreEqual(frozen.Commit, manifest.Release.Commit);
            var artifact = manifest.ForInstallation("win-x64", "zip");
            Assert.IsNotNull(artifact);
            Assert.AreEqual(frozen.Sha256, artifact.Sha256);
            Assert.IsNull(manifest.ForInstallation("linux-x64", "tar.gz"));
            var archive = await source.DownloadAsync(manifest, "win-x64", "zip", scratch, cancellationToken: deadline.Token);
            string root = Path.Combine(scratch, "installed");
            var installed = await WindowsVerifiedVersions.InstallAsync(root, archive.Path, envelope, key, origin, "preview", deadline.Token);
            Assert.AreEqual(frozen.Commit, installed.Version.Commit);
            var configuration = JsonSerializer.Deserialize<UpdatePreparationConfiguration>(
                await File.ReadAllBytesAsync(installed.Version.UpdateConfiguration, deadline.Token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            JsonElement first = await Check(installed.Version.UpdateConfiguration, "first", 0, "up_to_date");
            string accepted = Path.Combine(configuration.StateDirectory, "accepted-envelope.json");
            byte[] acceptedBytes = await File.ReadAllBytesAsync(accepted, deadline.Token);
            DateTime acceptedWrite = File.GetLastWriteTimeUtc(accepted);
            await Check(installed.Version.UpdateConfiguration, "unchanged", 0, "up_to_date");
            Assert.AreEqual(acceptedWrite, File.GetLastWriteTimeUtc(accepted));

            string wrong = Path.Combine(scratch, "wrong-platform.json");
            await File.WriteAllTextAsync(wrong, JsonSerializer.Serialize(configuration with { Platform = "linux-x64" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            await Check(wrong, "wrong-platform", 1, "failed");
            await Check(installed.Version.UpdateConfiguration, "recovered", 0, "up_to_date");
            Assert.AreEqual(installed.SelectionId, WindowsVerifiedVersions.InspectSelectionId(root));
            CollectionAssert.AreEqual(acceptedBytes, await File.ReadAllBytesAsync(accepted, deadline.Token));
            Assert.AreEqual(acceptedWrite, File.GetLastWriteTimeUtc(accepted));
            CollectionAssert.AreEqual(envelope, await source.FetchManifestAsync("preview", deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", platform = "win-x64", origin = origin.AbsoluteUri,
                sourceCommit = frozen.Commit, archiveSha256 = artifact.Sha256, manifestSha256 = manifest.PayloadSha256,
                manifest.Release.Sequence, installedNativeIdentityVerified = true, shippedHelperResult = first,
                installedPackagedHelperCheckedPublicFeed = true, unchangedCheckNoChurn = true,
                wrongPlatformRejected = true, recoveryVerified = true, installationSelectionUnchanged = true,
                nativeEditorRestarted = false, automaticUpdatingQualified = false, crossPlatformReady = false
            }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);

            async Task<JsonElement> Check(string path, string name, int exitCode, string status)
            {
                var result = await WindowsLauncherTests.Invoke(installed.Version.McpExecutable,
                    ["--check-update", "--configuration", path], scratch, deadline.Token, input: null);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + ".stdout.log"), result.Output, deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + ".stderr.log"), result.Error, deadline.Token);
                Assert.AreEqual(exitCode, result.ExitCode, result.Error);
                using var terminal = JsonDocument.Parse(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
                var value = terminal.RootElement.Clone();
                Assert.AreEqual(status, value.GetProperty("status").GetString());
                Assert.IsFalse(value.GetProperty("installationReady").GetBoolean());
                if (exitCode == 0)
                {
                    Assert.AreEqual(frozen.Commit, value.GetProperty("commit").GetString());
                    Assert.AreEqual(manifest.PayloadSha256, value.GetProperty("manifestSha256").GetString());
                }
                return value;
            }
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(evidence, "failure.txt"), error.ToString()); throw; }
        finally
        {
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch);
        }
    }
}
