using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsNativeLogTests
{
    [TestMethod]
    public async Task ObserveALiveWriterWithoutDenyingItsWriteHandle()
    {
        string root = Directory.CreateTempSubdirectory("kwnativelog-").FullName;
        try
        {
            string path = Path.Combine(root, "native.log");
            await using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                await writer.WriteAsync(Encoding.UTF8.GetBytes("checking\n")); await writer.FlushAsync();
                if (OperatingSystem.IsWindows())
                    await Assert.ThrowsExactlyAsync<IOException>(() => File.ReadAllTextAsync(path));
                Assert.AreEqual("checking\n", await WindowsNativeLog.ReadAsync(path, CancellationToken.None));
                await writer.WriteAsync(Encoding.UTF8.GetBytes("candidate_available\n")); await writer.FlushAsync();
                Assert.AreEqual("checking\ncandidate_available\n", await WindowsNativeLog.ReadAsync(path, CancellationToken.None));
                await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsNativeLog.ReadAsync(path, new(true)));
            }
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => WindowsNativeLog.ReadAsync(Path.Combine(root, "missing.log"), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
