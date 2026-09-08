using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignFileIntakeSessionTests
{
    private static async Task Isolated(Func<string, DesignRecoveryStore, StoredDesignRecovery, Task> test)
    {
        string root = Directory.CreateTempSubdirectory("kicad-intake-session-").FullName;
        try
        {
            var store = new DesignRecoveryStore(Path.Combine(root, "recovery.json"));
            var saved = store.Save(DesignRecoveryStoreTests.Fixture(), null);
            await test(root, store, saved);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task RunsBetweenCallsRecoversInvalidDesignAndStopsBeforeReturning() => Isolated(async (root, store, saved) =>
    {
        string path = Path.Combine(root, "design.xml"); await File.WriteAllBytesAsync(path, saved.State.DesiredFileBytes);
        await using var session = new DesignFileIntakeSession(store, saved.State.InstanceId, path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = await session.WaitAsync(0, deadline.Token); Assert.AreEqual(DesignFileIntakePhase.Watching, initial.Phase);
        string replacement = Path.Combine(root, "replacement.xml");
        await File.WriteAllBytesAsync(replacement, [0xff]); File.Move(replacement, path, true);
        var invalid = await session.WaitAsync(initial.Sequence, deadline.Token);
        Assert.AreEqual(DesignFileIntakePhase.InvalidDesign, invalid.Phase);
        CollectionAssert.AreEqual(new byte[] { 0xff }, store.Read()!.State.DesiredFileBytes);
        await File.WriteAllBytesAsync(replacement, saved.State.DesiredFileBytes); File.Move(replacement, path, true);
        var corrected = await session.WaitAsync(invalid.Sequence, deadline.Token);
        while (corrected.Phase == DesignFileIntakePhase.InvalidDesign) corrected = await session.WaitAsync(corrected.Sequence, deadline.Token);
        Assert.AreEqual(DesignFileIntakePhase.Watching, corrected.Phase);
        await session.DisposeAsync();
        Assert.AreEqual(DesignFileIntakePhase.Stopped, session.Inspect().Phase);
        string token = store.Read()!.RevisionToken;
        await File.WriteAllBytesAsync(path, [0xff]);
        Assert.AreEqual(token, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public Task PersistenceFailurePausesUntilExplicitResumeAndWaitCancellationDoesNotStopIntake() => Isolated(async (root, store, saved) =>
    {
        string path = Path.Combine(root, "missing.xml");
        await using var session = new DesignFileIntakeSession(store, saved.State.InstanceId, path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await session.WaitAsync(0, deadline.Token); Assert.AreEqual(DesignFileIntakePhase.Paused, paused.Phase);
        await File.WriteAllBytesAsync(path, saved.State.DesiredFileBytes);
        Assert.ThrowsExactly<AutomationException>(() => session.Resume(paused.Sequence + 1));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitAsync(paused.Sequence, cancelled.Token));
        Assert.AreEqual(DesignFileIntakePhase.Paused, session.Inspect().Phase);
        session.Resume(paused.Sequence);
        var resumed = await session.WaitAsync(paused.Sequence, deadline.Token);
        Assert.AreEqual(DesignFileIntakePhase.Watching, resumed.Phase);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        await Assert.ThrowsExactlyAsync<AutomationException>(() => session.WaitAsync(ulong.MaxValue, deadline.Token));
    });
}
