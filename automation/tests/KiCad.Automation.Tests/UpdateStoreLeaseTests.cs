using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateStoreLeaseTests
{
    [TestMethod]
    public async Task ContendingPreparationsWaitCancelAndRecoverWithoutRemovingTheLock()
    {
        string root = Directory.CreateTempSubdirectory("kicad-lease-").FullName;
        try
        {
            string path = Path.Combine(root, "registration.lock");
            using var first = await UpdateStoreLease.AcquireAsync(path, default);
            using (var cancel = new CancellationTokenSource(100))
                await Assert.ThrowsAsync<OperationCanceledException>(() => UpdateStoreLease.AcquireAsync(path, cancel.Token));
            Assert.IsTrue(File.Exists(path));
            var second = UpdateStoreLease.AcquireAsync(path, default);
            Assert.IsFalse(second.IsCompleted);
            first.Dispose();
            using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(File.Exists(path));
            await Assert.ThrowsExactlyAsync<IOException>(() => UpdateStoreLease.AcquireAsync(path, default, TimeSpan.FromMilliseconds(100)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MissingParentsAndUnrelatedIoErrorsAreNotRetried()
    {
        string root = Directory.CreateTempSubdirectory("kicad-lease-").FullName;
        try
        {
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => UpdateStoreLease.AcquireAsync(Path.Combine(root, "missing/lock"), default));
            Assert.IsFalse(UpdateStoreLease.Contention(new IOException("disk full", unchecked((int)0x80070070))));
            Assert.IsFalse(UpdateStoreLease.Contention(new IOException("access denied", unchecked((int)0x80070005))));
        }
        finally { Directory.Delete(root, true); }
    }
}
