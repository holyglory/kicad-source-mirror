using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsVersionSelectionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NonWindowsCannotSupplyWindowsSelectionEvidence()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("The other-platform guard does not run on Windows."); return; }
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => WindowsVersionSelection.InitializeAsync("/tmp/manager", new string('1', 64), default));
    }

    [TestMethod]
    public async Task NativeSelectionPreservesPayloadsAndRecoversCancelledLockedAndStaleOperations()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows file replacement and sharing."); return; }
        string root = Directory.CreateTempSubdirectory("kwselect-").FullName;
        string manager = Path.Combine(root, "manager");
        string firstDigest = new('1', 64), secondDigest = new('2', 64);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            foreach (string digest in new[] { firstDigest, secondDigest })
            {
                string directory = Directory.CreateDirectory(Path.Combine(root, "versions", digest, "payload")).FullName;
                await File.WriteAllTextAsync(Path.Combine(directory, "sentinel"), "Retained payload " + digest);
            }
            var initial = await WindowsVersionSelection.InitializeAsync(manager, firstDigest, deadline.Token);
            Assert.AreEqual(initial, WindowsVersionSelection.Inspect(manager));
            await Assert.ThrowsExactlyAsync<IOException>(() => WindowsVersionSelection.InitializeAsync(manager, firstDigest, deadline.Token));
            Guid operation = Guid.NewGuid();
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsVersionSelection.SwitchAsync(manager, initial,
                    secondDigest, operation, cancel.Token, cancel.Cancel));
            Assert.AreEqual(initial, WindowsVersionSelection.Inspect(manager));
            Assert.IsTrue(File.Exists(Path.Combine(manager, "operations", operation.ToString("D") + ".json")));
            var selected = await WindowsVersionSelection.SwitchAsync(manager, initial, secondDigest, operation, deadline.Token);
            Assert.AreEqual(selected, WindowsVersionSelection.Inspect(manager));
            Assert.AreEqual(selected, await WindowsVersionSelection.SwitchAsync(manager, initial, secondDigest, operation, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVersionSelection.SwitchAsync(manager, initial, firstDigest, operation, deadline.Token));

            Guid rollbackId = Guid.NewGuid();
            string current = Path.Combine(manager, "current.json");
            byte[] original = await File.ReadAllBytesAsync(current, deadline.Token);
            using (var locked = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAsync<IOException>(() => WindowsVersionSelection.SwitchAsync(manager, selected, firstDigest, rollbackId, deadline.Token));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(current, deadline.Token));
            var rolledBack = await WindowsVersionSelection.SwitchAsync(manager, selected, firstDigest, rollbackId, deadline.Token);
            Assert.AreEqual(initial.VersionTarget, rolledBack.VersionTarget);
            Assert.AreNotEqual(initial.SelectionId, rolledBack.SelectionId);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVersionSelection.SwitchAsync(manager, initial, secondDigest, operation, deadline.Token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsVersionSelection.SwitchAsync(manager, rolledBack, new string('g', 64), Guid.NewGuid(), deadline.Token));

            async Task<bool> Attempt()
            {
                try { await WindowsVersionSelection.SwitchAsync(manager, rolledBack, secondDigest, Guid.NewGuid(), deadline.Token); return true; }
                catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
            }
            bool[] winners = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
            Assert.AreEqual(1, winners.Count(value => value), "Only one request with the same expected selection may win.");
            var final = WindowsVersionSelection.Inspect(manager);
            Assert.AreEqual("versions/" + secondDigest + "/payload", final.VersionTarget);
            foreach (string digest in new[] { firstDigest, secondDigest })
                Assert.AreEqual("Retained payload " + digest, await File.ReadAllTextAsync(Path.Combine(root, "versions", digest, "payload/sentinel"), deadline.Token));
            var bytes = await File.ReadAllBytesAsync(current, deadline.Token);
            await File.WriteAllTextAsync(current, "{\"schemaVersion\":1,\"schemaVersion\":1}", deadline.Token);
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsVersionSelection.Inspect(manager));
            await File.WriteAllBytesAsync(current, bytes, deadline.Token);
            Assert.AreEqual(final, WindowsVersionSelection.Inspect(manager));

            string evidence = Directory.CreateDirectory(Path.Combine(
                Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
                "windows-version-selection")).FullName;
            string result = Path.Combine(evidence, "result.json");
            await File.WriteAllTextAsync(result, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", nativeWindowsRecordReplacement = true,
                cancelledRetry = true, lockedRecordRecovery = true, staleOperationRejected = true,
                concurrentWinners = winners.Count(value => value), retainedPayloadsUnchanged = true,
                realInstallationVerified = false, nativeEditorRestarted = false
            }), deadline.Token);
            TestContext.AddResultFile(result);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConcurrentInitializationCannotDeleteTheWinningStore()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows file sharing."); return; }
        string root = Directory.CreateTempSubdirectory("kwinit-").FullName;
        try
        {
            string digest = new('1', 64), manager = Path.Combine(root, "manager");
            Directory.CreateDirectory(Path.Combine(root, "versions", digest, "payload"));
            async Task<WindowsSelectedVersion?> TryInitialize()
            {
                try { return await WindowsVersionSelection.InitializeAsync(manager, digest, default); }
                catch (IOException) { return null; }
            }
            var results = await Task.WhenAll(Task.Run(TryInitialize), Task.Run(TryInitialize));
            Assert.AreEqual(1, results.Count(result => result is not null));
            Assert.AreEqual(results.Single(result => result is not null), WindowsVersionSelection.Inspect(manager));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "versions", digest, "payload")));
        }
        finally { Directory.Delete(root, true); }
    }
}
