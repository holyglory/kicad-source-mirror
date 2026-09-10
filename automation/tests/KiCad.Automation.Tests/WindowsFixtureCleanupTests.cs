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
