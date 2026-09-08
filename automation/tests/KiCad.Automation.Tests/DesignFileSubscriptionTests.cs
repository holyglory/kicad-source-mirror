using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignFileSubscriptionTests
{
    [TestMethod]
    public async Task InitialStateAndBurstsCoalesceWithoutLosingRecovery()
    {
        using var fixture = new Fixture();
        using var watch = new DesignFileSubscription(fixture.Path);
        for (int i = 0; i < 10000; i++) watch.NotifyChange(fixture.Path);
        watch.NotifyError(new InternalBufferOverflowException());
        watch.NotifyChange(fixture.Path);
        var result = await watch.ReceiveAsync(fixture.Deadline.Token);
        Assert.AreEqual(fixture.Path, result.Path);
        Assert.AreEqual(10002UL, result.Sequence);
        Assert.AreEqual(DesignFileChangeReason.InitialStateRequired | DesignFileChangeReason.FileChanged
            | DesignFileChangeReason.RecoveryRequired | DesignFileChangeReason.ObservationStopped, result.Reasons);
        Assert.AreEqual("sync_watch_overflow", result.ErrorCode);
        Assert.AreEqual("sync_watch_stopped", (await Assert.ThrowsExactlyAsync<AutomationException>(
            () => watch.ReceiveAsync(fixture.Deadline.Token))).Code);
    }

    [TestMethod]
    public async Task UnrelatedNamesAndCancelledWaitsDoNotConsumeChangesOrStopObservation()
    {
        using var fixture = new Fixture();
        using var watch = new DesignFileSubscription(fixture.Path);
        Assert.AreEqual(DesignFileChangeReason.InitialStateRequired,
            (await watch.ReceiveAsync(fixture.Deadline.Token)).Reasons);
        watch.NotifyChange(fixture.Path + ".tmp");
        watch.NotifyChange(System.IO.Path.Combine(fixture.Directory, "other.xml"));
        using var cancellation = new CancellationTokenSource();
        var waiting = watch.ReceiveAsync(cancellation.Token);
        Assert.IsFalse(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
        watch.NotifyChange(fixture.Path);
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => watch.ReceiveAsync(alreadyCancelled.Token));
        Assert.AreEqual(2UL, (await watch.ReceiveAsync(fixture.Deadline.Token)).Sequence);
    }

    [TestMethod]
    public async Task DisposeWakesWaitersAndFailureRequiresReattachment()
    {
        using var fixture = new Fixture();
        using (var watch = new DesignFileSubscription(fixture.Path))
        {
            await watch.ReceiveAsync(fixture.Deadline.Token);
            var waiting = watch.ReceiveAsync(fixture.Deadline.Token);
            watch.Dispose();
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await waiting);
        }
        using (var watch = new DesignFileSubscription(fixture.Path))
        {
            await watch.ReceiveAsync(fixture.Deadline.Token);
            watch.NotifyError(new IOException("fixture failure"));
            Assert.AreEqual("sync_watch_failed", (await watch.ReceiveAsync(fixture.Deadline.Token)).ErrorCode);
        }
        using var reattached = new DesignFileSubscription(fixture.Path);
        Assert.AreEqual(DesignFileChangeReason.InitialStateRequired,
            (await reattached.ReceiveAsync(fixture.Deadline.Token)).Reasons);
    }

    [TestMethod]
    [DataRow("create")]
    [DataRow("write")]
    [DataRow("replace")]
    [DataRow("rename-away")]
    [DataRow("delete")]
    public async Task RealFilesystemChangesInvalidateTheExactDesignPath(string operation)
    {
        using var fixture = new Fixture();
        if (operation != "create") await File.WriteAllTextAsync(fixture.Path, "before");
        string temporary = fixture.Path + ".tmp";
        if (operation == "replace") await File.WriteAllTextAsync(temporary, "after");
        using var watch = new DesignFileSubscription(fixture.Path);
        await watch.ReceiveAsync(fixture.Deadline.Token);
        var waiting = watch.ReceiveAsync(fixture.Deadline.Token);
        switch (operation)
        {
            case "create": case "write": await File.WriteAllTextAsync(fixture.Path, "after"); break;
            case "replace": File.Move(temporary, fixture.Path, overwrite: true); break;
            case "rename-away": File.Move(fixture.Path, temporary); break;
            case "delete": File.Delete(fixture.Path); break;
        }
        var result = await waiting;
        Assert.AreEqual(fixture.Path, result.Path);
        Assert.IsTrue(result.Reasons.HasFlag(DesignFileChangeReason.FileChanged));
        Assert.IsNull(result.ErrorCode);
    }

    [TestMethod]
    public async Task ReplacingTheContainingDirectoryStopsTheOldWatch()
    {
        using var fixture = new Fixture();
        string child = System.IO.Path.Combine(fixture.Directory, "design");
        System.IO.Directory.CreateDirectory(child);
        using var watch = new DesignFileSubscription(System.IO.Path.Combine(child, "design.xml"));
        await watch.ReceiveAsync(fixture.Deadline.Token);
        var waiting = watch.ReceiveAsync(fixture.Deadline.Token);
        System.IO.Directory.Move(child, child + "-old");
        System.IO.Directory.CreateDirectory(child);
        var result = await waiting;
        Assert.IsTrue(result.Reasons.HasFlag(DesignFileChangeReason.ObservationStopped));
        Assert.IsTrue(result.Reasons.HasFlag(DesignFileChangeReason.RecoveryRequired));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("kicad-file-watch-").FullName;
        public string Path => System.IO.Path.Combine(Directory, "design.xml");
        public CancellationTokenSource Deadline { get; } = new(TimeSpan.FromSeconds(10));
        public void Dispose()
        {
            Deadline.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
