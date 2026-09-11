using KiCad.Automation.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WindowsFixtureCleanupTests
{
    [TestMethod]
    public void OnlyDocumentedAccessAndSharingErrorsAreRetryable()
    {
        Assert.IsTrue(WindowsFixtureCleanup.IsTransient(new IOException("sharing", unchecked((int)0x80070020))));
        Assert.IsTrue(WindowsFixtureCleanup.IsTransient(new UnauthorizedAccessException()));
        Assert.IsFalse(WindowsFixtureCleanup.IsTransient(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.IsFalse(WindowsFixtureCleanup.IsTransient(new InvalidOperationException()));
    }

    [TestMethod]
    public async Task NativeReadonlyHistoryCleanupPreservesUnrelatedTreesAndAttributes()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Native Windows history deletion."); return; }
        string owned = Directory.CreateTempSubdirectory("kwhistory-").FullName;
        string other = Directory.CreateTempSubdirectory("kwhistory-other-").FullName;
        string Object(string root)
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, ".history", ".git", "objects", "ab")).FullName;
            string file = Path.Combine(directory, new string('c', 38));
            File.WriteAllText(file, "Read-only test history object"); File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Archive);
            return file;
        }
        string first = Object(owned), second = Object(other);
        try
        {
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(owned);
            Assert.IsFalse(Directory.Exists(owned));
            Assert.AreEqual("Read-only test history object", await File.ReadAllTextAsync(second));
            Assert.IsTrue(File.GetAttributes(second).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (Directory.Exists(owned)) await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(owned);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(other);
        }
    }

    [TestMethod]
    public async Task NativeUnknownReadonlyFileIsNotForceDeleted()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Native Windows readonly negative."); return; }
        string root = Directory.CreateTempSubdirectory("kwreadonly-negative-").FullName;
        string file = Path.Combine(root, "not-history.txt");
        try
        {
            await File.WriteAllTextAsync(file, "Preserve this negative control"); File.SetAttributes(file, FileAttributes.ReadOnly);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root, stop.Token));
            Assert.IsTrue(File.Exists(file)); Assert.IsTrue(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            // Undo only this test's explicit negative-control setup.
            File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task NativeRuntimeCleanupHasAnExactInstanceBoundary()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Native Windows cleanup boundary."); return; }
        string parent = Path.Combine(Path.GetTempPath(), "kicad-automation");
        string owned = Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("D"))).FullName;
        string other = Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("D"))).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(owned, "native.log"), "Fixture log");
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => WindowsFixtureCleanup.RemoveOwnedRuntimeDirectoryAsync(parent));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => WindowsFixtureCleanup.RemoveOwnedRuntimeDirectoryAsync(Path.GetTempPath()));
            await WindowsFixtureCleanup.RemoveOwnedRuntimeDirectoryAsync(owned);
            Assert.IsFalse(Directory.Exists(owned)); Assert.IsTrue(Directory.Exists(other));
        }
        finally { if (Directory.Exists(owned)) Directory.Delete(owned, true); Directory.Delete(other, true); }
    }

    [TestMethod]
    public async Task NativeSharingReleaseAndCancellationLeaveUnrelatedFilesAlone()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows file-sharing semantics."); return; }
        string root = Directory.CreateTempSubdirectory("kwcleanup-").FullName;
        string other = Directory.CreateTempSubdirectory("kwpreserved-").FullName;
        try
        {
            string file = Path.Combine(root, "owned.exe");
            await File.WriteAllTextAsync(file, "synthetic locked fixture");
            using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var cancel = new CancellationTokenSource(150))
                await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root, cancel.Token));
            Assert.IsTrue(File.Exists(file)); Assert.IsTrue(Directory.Exists(other));
            var removal = WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
            Assert.IsFalse(removal.IsCompleted, "A held file must not be discarded or treated as removed.");
            hold.Dispose(); await removal;
            Assert.IsFalse(Directory.Exists(root)); Assert.IsTrue(Directory.Exists(other));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(Path.GetTempPath()));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(Path.GetPathRoot(other)!));
        }
        finally
        {
            if (Directory.Exists(root)) await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(other);
        }
    }
}
