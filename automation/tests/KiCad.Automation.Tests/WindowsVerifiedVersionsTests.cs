using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsVerifiedVersionsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NonWindowsCannotCreateAVerifiedWindowsStore()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("The other-platform guard does not run on Windows."); return; }
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => WindowsVerifiedVersions.CreateAsync("/tmp/root", "/tmp/archive",
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, new Uri("https://fixture.invalid/"), "preview"));
    }

    [TestMethod]
    public async Task NativeVerifiedStorePreservesTrustVersionsAndCheckpointAcrossRollback()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires actual Windows executable and file semantics."); return; }
        string scratch = Directory.CreateTempSubdirectory("kwversions-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!, "windows-verified-versions")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            byte[] executable = await WindowsUpdateStagerTests.Compile(scratch, evidence, deadline.Token);
            using var zip = WindowsUpdateStagerTests.Zip(executable, "normal");
            byte[] archive = zip.ToArray(); string path = Path.Combine(scratch, "native-fixture.zip");
            await File.WriteAllBytesAsync(path, archive, deadline.Token);
            var artifact = new UpdateArtifact("win-x64", "zip", "fixture.zip", archive.LongLength, Convert.ToHexStringLower(SHA256.HashData(archive)));
            UpdateRelease Release(long sequence) => new(1, "kicad-codex", "preview", sequence, "synthetic-version-" + sequence, new string('1', 40), [artifact]);
            byte[] firstEnvelope = UpdateManifestCodec.Sign(Release(1), key), secondEnvelope = UpdateManifestCodec.Sign(Release(2), key);
            string root = Path.Combine(scratch, "installation");
            var initial = await WindowsVerifiedVersions.CreateAsync(root, path, firstEnvelope, key.ExportSubjectPublicKeyInfo(), new Uri("https://fixture.invalid/"), "preview", deadline.Token);
            Assert.AreEqual(initial, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
            await Assert.ThrowsExactlyAsync<IOException>(() => WindowsVerifiedVersions.CreateAsync(root, path, firstEnvelope,
                key.ExportSubjectPublicKeyInfo(), new Uri("https://fixture.invalid/"), "preview", deadline.Token));
            var registered = await WindowsVerifiedVersions.RegisterAsync(root, initial.SelectionId, path, secondEnvelope, deadline.Token);
            Assert.IsFalse(registered.Reused);
            Assert.AreEqual(initial, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token), "Registration cannot change selection.");
            Assert.IsTrue((await WindowsVerifiedVersions.RegisterAsync(root, initial.SelectionId, path, secondEnvelope, deadline.Token)).Reused);
            using (var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.RegisterAsync(root, initial.SelectionId, path,
                    UpdateManifestCodec.Sign(Release(3), wrongKey), deadline.Token));
            string checkpoint = Path.Combine(root, "state/accepted-envelope.json");
            await File.WriteAllBytesAsync(checkpoint, secondEnvelope, deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.RegisterAsync(root, initial.SelectionId, path, firstEnvelope, deadline.Token));
            Guid operation = Guid.NewGuid();
            var selected = await WindowsVerifiedVersions.ActivateAsync(root, initial.SelectionId, registered.Version.ManifestSha256, operation, deadline.Token);
            Assert.AreEqual(selected, await WindowsVerifiedVersions.ActivateAsync(root, initial.SelectionId, registered.Version.ManifestSha256, operation, deadline.Token));
            Assert.AreEqual(selected, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
            Assert.AreEqual(initial.Version, await WindowsVerifiedVersions.InspectExecutableAsync(root, initial.Version.NativeExecutable, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.ActivateAsync(root, selected.SelectionId, initial.Version.ManifestSha256, Guid.NewGuid(), deadline.Token));
            string damagedCandidate = Path.Combine(registered.Version.VersionDirectory, "bin/nng.dll");
            byte[] candidateBytes = await File.ReadAllBytesAsync(damagedCandidate, deadline.Token);
            await File.WriteAllTextAsync(damagedCandidate, "damaged new version", deadline.Token);
            Guid rollback = Guid.NewGuid();
            var restored = await WindowsVerifiedVersions.RollbackAsync(root, selected.SelectionId, operation, rollback, deadline.Token);
            Assert.AreEqual(initial.Version, restored.Version); Assert.AreNotEqual(initial.SelectionId, restored.SelectionId);
            Assert.AreEqual(restored, await WindowsVerifiedVersions.RollbackAsync(root, selected.SelectionId, operation, rollback, deadline.Token));
            Assert.AreEqual("damaged new version", await File.ReadAllTextAsync(damagedCandidate, deadline.Token), "Rollback must preserve the failed candidate for diagnosis.");
            await File.WriteAllBytesAsync(damagedCandidate, candidateBytes, deadline.Token);
            CollectionAssert.AreEqual(secondEnvelope, await File.ReadAllBytesAsync(checkpoint, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.ActivateAsync(root, initial.SelectionId, registered.Version.ManifestSha256, operation, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.RegisterAsync(root, restored.SelectionId, path, firstEnvelope, deadline.Token));

            string dll = Path.Combine(initial.Version.VersionDirectory, "bin/nng.dll");
            byte[] original = await File.ReadAllBytesAsync(dll, deadline.Token);
            await File.WriteAllTextAsync(dll, "accidental changed payload", deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
            await File.WriteAllBytesAsync(dll, original, deadline.Token);
            Assert.AreEqual(restored, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
            string extra = Path.Combine(initial.Version.VersionDirectory, "unexpected.txt");
            await File.WriteAllTextAsync(extra, "extra", deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVerifiedVersions.InspectExecutableAsync(root, initial.Version.NativeExecutable, deadline.Token));
            File.Delete(extra);
            Assert.AreEqual(initial.Version, await WindowsVerifiedVersions.InspectExecutableAsync(root, initial.Version.NativeExecutable, deadline.Token));
            var before = await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token);
            await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsVerifiedVersions.RegisterAsync(root, before.SelectionId, path,
                UpdateManifestCodec.Sign(Release(3), key), new CancellationToken(true)));
            Assert.AreEqual(before, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
            using (var waitZip = WindowsUpdateStagerTests.Zip(executable, "wait"))
            {
                byte[] waitBytes = waitZip.ToArray(); string waitingPath = Path.Combine(scratch, "waiting.zip");
                await File.WriteAllBytesAsync(waitingPath, waitBytes, deadline.Token);
                var waitingArtifact = artifact with { FileName = "waiting.zip", Bytes = waitBytes.LongLength,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(waitBytes)) };
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsVerifiedVersions.RegisterAsync(root, before.SelectionId,
                    waitingPath, UpdateManifestCodec.Sign(Release(3) with { Artifacts = [waitingArtifact] }, key), cancel.Token));
                Assert.AreEqual(before, await WindowsVerifiedVersions.InspectCurrentAsync(root, deadline.Token));
                using var failure = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "staging"),
                    "failure.json", SearchOption.AllDirectories).Single(), deadline.Token));
                Assert.AreEqual("cancelled", failure.RootElement.GetProperty("status").GetString());
                CollectionAssert.AreEqual(secondEnvelope, await File.ReadAllBytesAsync(checkpoint, deadline.Token));
            }
            Assert.IsEmpty(Directory.GetDirectories(Path.Combine(root, "versions"), ".prepare-*"));
            string receipt = Path.Combine(evidence, "result.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", syntheticExecutableFixture = true,
                publisherAndPayloadVerified = true, registeredWithoutSwitching = true, previousExecutablePreserved = true,
                exactActivationRetry = true, rollbackPreservedCheckpoint = true, payloadDriftRejected = true,
                installationReady = false, nativeEditorRestarted = false
            }), deadline.Token);
            TestContext.AddResultFile(receipt);
        }
        finally { await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch); }
    }
}
