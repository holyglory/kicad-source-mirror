using KiCad.Automation.Model;
using KiCad.Automation.Native;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class InstanceRegistryTests
{
    [TestMethod]
    public async Task PlatformLocalLaunchReceiptUsesTheSameEndpointAsNativeStartup()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-local-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "project.kicad_pro") };
            string endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(NativeIpcEndpoint.RuntimeDirectory(transport.InstanceId), "api.sock"));
            var launch = new UnverifiedInstanceLaunch(transport.InstanceId, transport.ProjectPath, endpoint, 12345, DateTimeOffset.UtcNow);
            string receipt = await WriteLaunch(state, launch);
            var registry = new InstanceRegistry(transport, state);
            var attached = await registry.ReattachAsync(transport.InstanceId);
            Assert.AreEqual(endpoint, attached.Endpoint); Assert.AreEqual(launch.ProcessId, attached.ProcessId);
            Assert.IsFalse(File.Exists(receipt));
            Assert.AreEqual(transport.Epoch, registry.Client(transport.InstanceId).Epoch);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task InterruptedLaunchIsUnverifiedUntilIdentityAndProjectAreConfirmed()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            var launch = new UnverifiedInstanceLaunch(transport.InstanceId, transport.ProjectPath,
                LaunchEndpoint(transport.InstanceId), 12345, DateTimeOffset.UtcNow);
            string receipt = await WriteLaunch(state, launch);
            var registry = new InstanceRegistry(transport, state);
            Assert.AreEqual(0, registry.List().Count);
            CollectionAssert.AreEqual(new[] { launch }, (await registry.PendingLaunchesAsync()).ToArray());
            var attached = await registry.ReattachAsync(launch.InstanceId);
            Assert.AreEqual(launch.ProcessId, attached.ProcessId);
            Assert.AreEqual(transport.Epoch, attached.Epoch);
            Assert.AreEqual(launch.ProjectPath, attached.ProjectPath);
            Assert.AreEqual(1, registry.List().Count);
            Assert.IsFalse(File.Exists(receipt));
            Assert.AreEqual(0, (await registry.PendingLaunchesAsync()).Count);
            var restarted = new InstanceRegistry(transport, state);
            Assert.AreEqual(attached.ProcessId, (await restarted.ReattachAsync(launch.InstanceId)).ProcessId);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    [DataRow(false, "instance_changed")]
    [DataRow(true, "instance_mismatch")]
    public async Task LaunchMismatchPreservesReceiptWithoutRegistering(bool wrongIdentity, string code)
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            string id = wrongIdentity ? Guid.NewGuid().ToString("D") : transport.InstanceId;
            var launch = new UnverifiedInstanceLaunch(id, Path.Combine(state, "other.kicad_pro"),
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow);
            string receipt = await WriteLaunch(state, launch);
            var registry = new InstanceRegistry(transport, state);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual(code, error.Code);
            Assert.AreEqual(0, registry.List().Count);
            Assert.IsTrue(File.Exists(receipt));
            Assert.IsFalse(File.Exists(Path.Combine(state, id + ".json")));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task LaunchReceiptCannotBypassPreviouslyVerifiedEpoch()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            string id = transport.InstanceId;
            var launch = new UnverifiedInstanceLaunch(id, transport.ProjectPath,
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow);
            await new InstanceRegistry(transport, state).AttachAsync(launch.Endpoint, id);
            string receipt = await WriteLaunch(state, launch);
            transport.Epoch = "replacement-process";
            var registry = new InstanceRegistry(transport, state);
            Assert.AreEqual(0, (await registry.PendingLaunchesAsync()).Count);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("instance_changed", error.Code);
            Assert.IsTrue(File.Exists(receipt));
            Assert.AreEqual(0, registry.List().Count);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task MalformedReceiptDoesNotBecomeAConnection()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport();
            string id = transport.InstanceId;
            var registry = new InstanceRegistry(transport, state);
            var missing = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("unknown_instance", missing.Code);
            string receipt = await WriteLaunch(state, new(id, "relative.kicad_pro",
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow));
            var invalid = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("invalid_registry", invalid.Code);
            await File.WriteAllTextAsync(receipt, "{invalid");
            invalid = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("invalid_registry", invalid.Code);
            Assert.AreEqual(0, registry.List().Count);
        }
        finally { Directory.Delete(state, true); }
    }

    private static async Task<string> WriteLaunch(string state, UnverifiedInstanceLaunch launch)
    {
        string directory = Directory.CreateDirectory(Path.Combine(state, "launches")).FullName;
        string path = Path.Combine(directory, launch.InstanceId + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(launch));
        return path;
    }

    [TestMethod]
    public async Task CorruptSavedSessionDoesNotFallBackToLaunchReceipt()
    {
        string state = Directory.CreateTempSubdirectory("kicad-launch-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            string id = transport.InstanceId;
            await WriteLaunch(state, new(id, transport.ProjectPath,
                LaunchEndpoint(id), null, DateTimeOffset.UtcNow));
            await File.WriteAllTextAsync(Path.Combine(state, id + ".json"), "{}");
            var registry = new InstanceRegistry(transport, state);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.ReattachAsync(id));
            Assert.AreEqual("invalid_registry", error.Code);
            Assert.IsNull(transport.LastRequest, "Invalid records must fail before native communication.");
            Assert.AreEqual(0, registry.List().Count);
            error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.SavedSessionsAsync());
            Assert.AreEqual("invalid_registry", error.Code);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task ReattachVerifiesSavedEpochAndSharesSerializedClient()
    {
        string state = Directory.CreateTempSubdirectory("kicad-registry-test-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "test.kicad_pro") };
            var first = new InstanceRegistry(transport, state);
            await first.AttachAsync(LaunchEndpoint(transport.InstanceId), transport.InstanceId);
            Assert.AreSame(first.Client(transport.InstanceId), first.Client(transport.InstanceId));
            var second = new InstanceRegistry(transport, state);
            Assert.AreEqual(0, second.List().Count);
            Assert.AreEqual(1, (await second.SavedSessionsAsync()).Count);
            await second.ReattachAsync(transport.InstanceId);
            Assert.AreEqual(1, second.List().Count);
            transport.Epoch = "restarted";
            Assert.AreEqual("fixture-epoch", (await second.SavedSessionsAsync())[0].Epoch,
                "Saved discovery must remain historical and must not implicitly contact or adopt a new peer.");
            AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                new InstanceRegistry(transport, state).ReattachAsync(transport.InstanceId));
            Assert.AreEqual("instance_changed", error.Code);
            Assert.IsTrue(File.Exists(Path.Combine(state, transport.InstanceId + ".json")));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task WrongIdentityDoesNotCreateARegistration()
    {
        string state = Directory.CreateTempSubdirectory("kicad-registry-test-").FullName;
        try
        {
            var registry = new InstanceRegistry(new NativeClientTests.FixtureTransport(), state);
            AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                registry.AttachAsync("ipc:///tmp/registry-fixture.sock", Guid.NewGuid().ToString("D")));
            Assert.AreEqual("instance_mismatch", error.Code);
            Assert.AreEqual(0, registry.List().Count);
            Assert.AreEqual(0, Directory.GetFiles(state).Length);
        }
        finally { Directory.Delete(state, true); }
    }

    private static string LaunchEndpoint(string id) => NativeIpcEndpoint.FromSocketPath(
        Path.Combine(NativeIpcEndpoint.RuntimeDirectory(id), "api.sock"));
}
