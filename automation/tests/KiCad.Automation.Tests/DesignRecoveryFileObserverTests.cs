using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignRecoveryFileObserverTests
{
    private static async Task Isolated(Func<string, DesignRecoveryStore, StoredDesignRecovery, Task> test)
    {
        string root = Directory.CreateTempSubdirectory("kicad-recovery-watch-").FullName;
        try
        {
            var state = DesignRecoveryStoreTests.Fixture(); var store = new DesignRecoveryStore(Path.Combine(root, "recovery.json"));
            var saved = store.Save(state, null); await test(root, store, saved);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task InitialAndAtomicSavedChangesRetainInvalidBytesAndBaseline() => Isolated(async (root, store, saved) =>
    {
        string path = Path.Combine(root, "design.xml"); await File.WriteAllBytesAsync(path, saved.State.DesiredFileBytes);
        using var watcher = new DesignRecoveryFileObserver(store, saved.State.InstanceId, path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = await watcher.ReceiveAsync(deadline.Token);
        Assert.IsFalse(initial.DesiredBytesChanged); Assert.AreEqual(saved.RevisionToken, initial.RecoveryRevisionToken);
        string replacement = Path.Combine(root, "replacement.xml"); byte[] invalid = [0xff, 0x3c];
        await File.WriteAllBytesAsync(replacement, invalid); File.Move(replacement, path, true);
        var changed = await watcher.ReceiveAsync(deadline.Token);
        Assert.IsTrue(changed.DesiredBytesChanged); Assert.AreEqual("invalid_desired_design", changed.ErrorCode);
        CollectionAssert.AreEqual(invalid, store.Read()!.State.DesiredFileBytes);
        Assert.AreEqual(SchematicDesignXml.Write(saved.State.Baseline, saved.State.KnowledgeLibraries),
            SchematicDesignXml.Write(store.Read()!.State.Baseline, saved.State.KnowledgeLibraries));
        await File.WriteAllBytesAsync(replacement, saved.State.DesiredFileBytes); File.Move(replacement, path, true);
        var restored = await watcher.ReceiveAsync(deadline.Token);
        Assert.IsNull(restored.ErrorCode); CollectionAssert.AreEqual(saved.State.DesiredFileBytes, store.Read()!.State.DesiredFileBytes);
    });

    [TestMethod]
    public Task MissingFileAndLockedRecoveryRetryWithoutLosingNotification() => Isolated(async (root, store, saved) =>
    {
        string path = Path.Combine(root, "design.xml");
        using var watcher = new DesignRecoveryFileObserver(store, saved.State.InstanceId, path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.AreEqual("design_file_io", (await watcher.ReceiveAsync(deadline.Token)).ErrorCode);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        byte[] invalid = Encoding.UTF8.GetBytes("<unfinished>"); await File.WriteAllBytesAsync(path, invalid);
        using (var held = new FileStream(Path.Combine(root, "recovery.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual("design_recovery_io", (await watcher.ReceiveAsync(deadline.Token)).ErrorCode);
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        var retried = await watcher.ReceiveAsync(deadline.Token);
        Assert.IsTrue(retried.DesiredBytesChanged); CollectionAssert.AreEqual(invalid, store.Read()!.State.DesiredFileBytes);
    });

    [TestMethod]
    public Task UnchangedFileRetainsChoicesAndChangedFileInvalidatesThem() => Isolated(async (root, store, saved) =>
    {
        var b = SchematicHierarchyTopologyTests.Fixture(); var x = b.Clone(); var n = b.Clone();
        x.Instances[0].Metadata.TitleBlock = new() { Title = "Desired" };
        n.Instances[0].Metadata.TitleBlock = new() { Title = "Native" };
        var baseline = saved.State.Baseline with { Schematic = b, SheetBindings = [], SymbolBindings = [] };
        byte[] desired = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline with { Schematic = x }, saved.State.KnowledgeLibraries));
        var state = saved.State with { Baseline = baseline, Observed = n, DesiredFileBytes = desired };
        var replaced = store.Save(state, saved.RevisionToken);
        var choices = DesignRecoveryStore.PlanHierarchy(state).Conflicts.ToDictionary(c => c.InstancePath, _ => SchematicConflictChoice.Xml);
        var resolved = store.ResolveHierarchy(replaced.RevisionToken, DesignRecoveryStore.HierarchySnapshotToken(state), choices);
        string path = Path.Combine(root, "design.xml"); await File.WriteAllBytesAsync(path, desired);
        using var watcher = new DesignRecoveryFileObserver(store, state.InstanceId, path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = await watcher.ReceiveAsync(deadline.Token);
        Assert.IsFalse(initial.ChoicesInvalidated); Assert.AreEqual(resolved.RevisionToken, initial.RecoveryRevisionToken);
        Assert.IsNotNull(store.Read()!.State.HierarchyResolution);
        string temporary = Path.Combine(root, "next.xml");
        await File.WriteAllBytesAsync(temporary, [.. desired, (byte)'\n']); File.Move(temporary, path, true);
        var updated = await watcher.ReceiveAsync(deadline.Token);
        Assert.IsTrue(updated.ChoicesInvalidated); Assert.IsNull(store.Read()!.State.HierarchyResolution);
        Assert.IsNull(updated.ErrorCode);
    });

    [TestMethod]
    public Task CancellationAndWrongInstanceDoNotWriteRecovery() => Isolated(async (root, store, saved) =>
    {
        string path = Path.Combine(root, "design.xml"); await File.WriteAllBytesAsync(path, [0xff]);
        Assert.ThrowsExactly<AutomationException>(() => new DesignRecoveryFileObserver(store, Guid.NewGuid(), path));
        using var watcher = new DesignRecoveryFileObserver(store, saved.State.InstanceId, path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => watcher.ReceiveAsync(cancelled.Token));
        Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await watcher.ReceiveAsync(deadline.Token)).DesiredBytesChanged);
    });
}
