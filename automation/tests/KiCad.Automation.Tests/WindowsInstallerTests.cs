using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsInstallerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeCompiledInstallCommandCreatesValidatedRootLaunchersWithoutStartingEditors()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows installer and launchers."); return; }
        string scratch = Directory.CreateTempSubdirectory("kwinstall-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!, "windows-installer")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            string launchers = await WindowsLauncherTests.Build(scratch, evidence, deadline.Token);
            byte[] executable = await WindowsUpdateStagerTests.Compile(scratch, evidence, deadline.Token);
            byte[] bytes;
            using (var sourceZip = WindowsUpdateStagerTests.Zip(executable, "normal"))
            {
                using (var zip = new ZipArchive(sourceZip, ZipArchiveMode.Update, leaveOpen: true))
                    foreach (string name in new[] { "kicad-automation-launcher.exe", "kicad-automation-mcp-launcher.exe" })
                    {
                        await using var input = File.OpenRead(Path.Combine(launchers, name));
                        await using var output = zip.CreateEntry("bin/" + name).Open();
                        await input.CopyToAsync(output, deadline.Token);
                    }
                bytes = sourceZip.ToArray();
            }
            string archive = Path.Combine(scratch, "fixture.zip"), publicKey = Path.Combine(scratch, "publisher.spki"), envelope = Path.Combine(scratch, "installed-envelope.json");
            await File.WriteAllBytesAsync(archive, bytes, deadline.Token);
            await File.WriteAllBytesAsync(publicKey, key.ExportSubjectPublicKeyInfo(), deadline.Token);
            var artifact = new UpdateArtifact("win-x64", "zip", "fixture.zip", bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "synthetic-installer-fixture", new string('1', 40), [artifact]);
            await File.WriteAllBytesAsync(envelope, UpdateManifestCodec.Sign(release, key), deadline.Token);
            string root = Path.Combine(scratch, "installation");
            string request = Path.Combine(scratch, "request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new LinuxInstallRequest(1, root, archive, envelope,
                "https://fixture.invalid/", "preview"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            var start = UpdateCommandTests.StartInfo();
            foreach (string argument in new[] { "--install-package", "--configuration", request, "--publisher-key", publicKey }) start.ArgumentList.Add(argument);
            var run = await WindowsLauncherTests.Invoke(start.FileName, start.ArgumentList.ToArray(), scratch, deadline.Token, input: null);
            await File.WriteAllTextAsync(Path.Combine(evidence, "install.stdout.log"), run.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "install.stderr.log"), run.Error, deadline.Token);
            Assert.AreEqual(0, run.ExitCode, run.Output + run.Error);
            using (var result = JsonDocument.Parse(run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]))
            {
                Assert.AreEqual("installed", result.RootElement.GetProperty("status").GetString());
                Assert.IsFalse(result.RootElement.GetProperty("automaticUpdatingQualified").GetBoolean());
                Assert.IsFalse(result.RootElement.GetProperty("nativeEditorRestarted").GetBoolean());
            }
            await WindowsVerifiedVersions.ValidateBootstrapAsync(root, deadline.Token);
            var initial = await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token);
            byte[] bootstrap = await File.ReadAllBytesAsync(Path.Combine(root, "launcher-bootstrap.json"), deadline.Token);
            var candidate = await WindowsVerifiedVersions.RegisterAsync(root, initial.SelectionId, archive,
                UpdateManifestCodec.Sign(release with { Sequence = 2, Version = "synthetic-second" }, key), deadline.Token);
            await WindowsVerifiedVersions.ActivateAsync(root, initial.SelectionId, candidate.Version.ManifestSha256, Guid.NewGuid(), deadline.Token);
            await WindowsVerifiedVersions.ValidateBootstrapAsync(root, deadline.Token);
            CollectionAssert.AreEqual(bootstrap, await File.ReadAllBytesAsync(Path.Combine(root, "launcher-bootstrap.json"), deadline.Token),
                "Selection must retain the initially installed bootstrap helper.");
            var selected = await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token);
            var launchStart = UpdateCommandTests.StartInfo();
            foreach (string argument in new[] { "--launch-installed", "--installation", root, "--target", "mcp", "--", "--runtime-info" })
                launchStart.ArgumentList.Add(argument);
            var launched = await WindowsLauncherTests.Invoke(launchStart.FileName, launchStart.ArgumentList.ToArray(), scratch, deadline.Token, input: null);
            Assert.AreEqual(0, launched.ExitCode, launched.Output + launched.Error);
            using (var launchResult = JsonDocument.Parse(launched.Output))
                Assert.AreEqual(selected.Version.McpExecutable.Replace('\\', '/'),
                    launchResult.RootElement.GetProperty("fixtureExecutablePath").GetString(), "The compiled helper must launch the selected version, not the initial bootstrap version.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "selected-launch.json"), launched.Output, deadline.Token);
            run = await WindowsLauncherTests.Invoke(start.FileName, start.ArgumentList.ToArray(), scratch, deadline.Token, input: null);
            Assert.AreEqual(1, run.ExitCode, "An existing installation must never be overwritten.");
            Assert.AreEqual(selected, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
            string launcher = Path.Combine(root, "kicad.exe");
            byte[] savedLauncher = await File.ReadAllBytesAsync(launcher, deadline.Token);
            await File.WriteAllTextAsync(launcher, "changed root launcher", deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.ValidateBootstrapAsync(root, deadline.Token));
            await File.WriteAllBytesAsync(launcher, savedLauncher, deadline.Token);
            await WindowsVerifiedVersions.ValidateBootstrapAsync(root, deadline.Token);
            using var incomplete = WindowsUpdateStagerTests.Zip(executable, "normal");
            string missingArchive = Path.Combine(scratch, "missing-launchers.zip"); byte[] missingBytes = incomplete.ToArray();
            await File.WriteAllBytesAsync(missingArchive, missingBytes, deadline.Token);
            var missing = artifact with { Bytes = missingBytes.LongLength, Sha256 = Convert.ToHexStringLower(SHA256.HashData(missingBytes)) };
            string rejectedRoot = Path.Combine(scratch, "must-not-publish");
            await Assert.ThrowsAsync<IOException>(() => WindowsVerifiedVersions.InstallAsync(rejectedRoot, missingArchive,
                UpdateManifestCodec.Sign(release with { Artifacts = [missing] }, key), key.ExportSubjectPublicKeyInfo(),
                new Uri("https://fixture.invalid/"), "preview", deadline.Token));
            Assert.IsFalse(Directory.Exists(rejectedRoot));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"),
                "{\"schemaVersion\":1,\"status\":\"passed\",\"compiledInstallerVerified\":true,\"rootBootstrapValidated\":true,\"syntheticNativePayload\":true,\"actualKiCadLaunchVerified\":false,\"automaticUpdatingQualified\":false}", deadline.Token);
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
        }
        finally { await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch); }
    }
}
