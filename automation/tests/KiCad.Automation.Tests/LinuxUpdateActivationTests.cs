using System.Text;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxUpdateActivationTests
{
    [TestMethod]
    [DataRow("kicad-activation-")]
    [DataRow("kicad-activation-Énergie space-")]
    public async Task SwitchingAndRollbackPreserveLiveFilesAndInvalidateOldRetries(string prefix)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux link activation requires Linux."); return; }
        using var fixture = new ActivationFixture(prefix);
        await LinuxUpdateActivation.InitializeAsync(fixture.Installation, fixture.First);
        string previous = LinuxUpdateActivation.InspectTarget(fixture.Installation);
        await using var live = File.OpenRead(Path.Combine(fixture.Installation, "current", "version.txt"));
        Guid operation = Guid.NewGuid();
        var switched = await LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation);
        Assert.AreEqual("switched", switched.Status);
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "current", "version.txt")));
        byte[] original = new byte[5];
        await live.ReadExactlyAsync(original);
        Assert.AreEqual("first", Encoding.UTF8.GetString(original));
        Assert.AreEqual(switched, await LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation));
        var rollback = await LinuxUpdateActivation.SwitchAsync(fixture.Installation, switched.Target, fixture.First, Guid.NewGuid());
        Assert.AreNotEqual(previous, rollback.Target);
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "current", "version.txt")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation));
        Assert.AreEqual(rollback.Target, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(fixture.Second, "version.txt")));
    }

    [TestMethod]
    public async Task ConflictingPointerOrPendingLinkIsPreservedUntilExplicitRepair()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux link activation requires Linux."); return; }
        using var fixture = new ActivationFixture();
        await LinuxUpdateActivation.InitializeAsync(fixture.Installation, fixture.First);
        string previous = LinuxUpdateActivation.InspectTarget(fixture.Installation);
        string current = Path.Combine(fixture.Installation, "current");
        File.Delete(current);
        await File.WriteAllTextAsync(current, "Pre-existing fixture data must not be overwritten.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, Guid.NewGuid()));
        Assert.AreEqual("Pre-existing fixture data must not be overwritten.", await File.ReadAllTextAsync(current));
        File.Move(current, current + ".preserved");
        Directory.CreateSymbolicLink(current, previous);
        Guid operation = Guid.NewGuid();
        string pending = Path.Combine(fixture.Installation, "current-" + operation.ToString("D") + ".pending");
        Directory.CreateSymbolicLink(pending, "selections/different-fixture");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation));
        Assert.AreEqual("selections/different-fixture", new FileInfo(pending).LinkTarget);
        Assert.AreEqual(previous, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        File.Delete(pending);
        var recovered = await LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation);
        Assert.AreEqual(recovered.Target, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        Assert.AreEqual("Pre-existing fixture data must not be overwritten.", await File.ReadAllTextAsync(current + ".preserved"));
    }

    [TestMethod]
    public async Task CancellationAtSwitchBoundaryCanResumeWithoutChangingLiveVersionEarly()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux link activation requires Linux."); return; }
        using var fixture = new ActivationFixture();
        await LinuxUpdateActivation.InitializeAsync(fixture.Installation, fixture.First);
        string previous = LinuxUpdateActivation.InspectTarget(fixture.Installation);
        Guid operation = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation,
                cancelled.Cancel, cancelled.Token));
        Assert.AreEqual(previous, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "current", "version.txt")));
        var recovered = await LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation);
        Assert.AreEqual(recovered.Target, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "current", "version.txt")));
    }

    [TestMethod]
    public async Task StaleIdsChangedOperationsCancellationAndLockedStoreNeverSwitch()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux link activation requires Linux."); return; }
        using var fixture = new ActivationFixture();
        await LinuxUpdateActivation.InitializeAsync(fixture.Installation, fixture.First);
        string previous = LinuxUpdateActivation.InspectTarget(fixture.Installation);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateActivation.SwitchAsync(fixture.Installation, "stale", fixture.Second, Guid.NewGuid()));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, Guid.NewGuid(), cancelled.Token));
        }
        using (var ownership = new FileStream(Path.Combine(fixture.Installation, "activation.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() =>
                LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, Guid.NewGuid()));
        Assert.AreEqual(previous, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        Guid operation = Guid.NewGuid();
        var switched = await LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.Second, operation);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LinuxUpdateActivation.SwitchAsync(fixture.Installation, previous, fixture.First, operation));
        Assert.AreEqual(switched.Target, LinuxUpdateActivation.InspectTarget(fixture.Installation));
        await Assert.ThrowsAsync<IOException>(() =>
            LinuxUpdateActivation.InitializeAsync(fixture.Installation, fixture.First));
    }

    private sealed class ActivationFixture : IDisposable
    {
        private readonly string root;
        public string First { get; }
        public string Second { get; }
        public string Installation => Path.Combine(root, "installation");
        public ActivationFixture(string prefix = "kicad-activation-")
        {
            root = Directory.CreateTempSubdirectory(prefix).FullName;
            First = Directory.CreateDirectory(Path.Combine(root, "version-one")).FullName;
            Second = Directory.CreateDirectory(Path.Combine(root, "version-two")).FullName;
            File.WriteAllText(Path.Combine(First, "version.txt"), "first");
            File.WriteAllText(Path.Combine(Second, "version.txt"), "second");
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
