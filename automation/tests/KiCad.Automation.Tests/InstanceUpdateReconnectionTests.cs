using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class InstanceUpdateReconnectionTests
{
    [TestMethod]
    public void OnlyAChangedLiveReplacementOfAKnownGoneOriginalCanProduceProof()
    {
        var origin = new UpdateOrigin(Endpoint("old"), "old");
        foreach (string status in new[] { "live", "unknown", "executable_mismatch" })
            Assert.IsNull(UpdateReplacementProof.FromInspection(origin, status, Guid.NewGuid(), Guid.NewGuid(),
                Path.GetFullPath("project.kicad_pro"), 1, 2, Endpoint("new"), "new"));
        Assert.IsNull(UpdateReplacementProof.FromInspection(null, "exited", Guid.NewGuid(), Guid.NewGuid(),
            Path.GetFullPath("project.kicad_pro"), 1, 2, Endpoint("new"), "new"));
        Assert.IsNull(UpdateReplacementProof.FromInspection(origin, "exited", Guid.NewGuid(), Guid.NewGuid(),
            Path.GetFullPath("project.kicad_pro"), 1, 2, Endpoint("new"), "old"));
        foreach (string status in new[] { "exited", "identity_changed" })
            Assert.IsNotNull(UpdateReplacementProof.FromInspection(origin, status, Guid.NewGuid(), Guid.NewGuid(),
                Path.GetFullPath("project.kicad_pro"), 1, 2, Endpoint("new"), "new"));
    }

    [TestMethod]
    public async Task ExplicitProofBindingRejectsOtherOperationsAndSurvivesMcpRestart()
    {
        string state = Directory.CreateTempSubdirectory("kicad-reconnect-").FullName;
        try
        {
            var peer = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "design.kicad_pro") };
            var registry = new InstanceRegistry(peer, state);
            var previous = await registry.AttachAsync(Endpoint("old"), peer.InstanceId);
            Guid operation = Guid.NewGuid();
            var proof = new UpdateReplacementProof(operation, Guid.Parse(peer.InstanceId), peer.ProjectPath,
                new(previous.Endpoint, previous.Epoch), 11, 22, Endpoint("new"), "new-epoch");
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(state, peer.InstanceId + ".json"));
            foreach (var invalid in new[] { proof with { InstanceId = Guid.NewGuid() }, proof with { OperationId = Guid.NewGuid() },
                proof with { ProjectPath = Path.Combine(state, "other.kicad_pro") }, proof with { Origin = proof.Origin with { Endpoint = Endpoint("other") } } })
                await Assert.ThrowsExactlyAsync<AutomationException>(() => InstanceUpdateReconnection.AdoptInspectedAsync(registry,
                    peer.InstanceId, operation, previous.Epoch, invalid, default));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(state, peer.InstanceId + ".json")));
            peer.Epoch = proof.Epoch;
            var first = await InstanceUpdateReconnection.AdoptInspectedAsync(registry, peer.InstanceId, operation, previous.Epoch, proof, default);
            Assert.IsFalse(first.Reused); Assert.AreEqual(proof.Epoch, registry.Client(peer.InstanceId).Epoch);
            var restarted = new InstanceRegistry(peer, state);
            var second = await InstanceUpdateReconnection.AdoptInspectedAsync(restarted, peer.InstanceId, operation, previous.Epoch, proof, default);
            Assert.IsTrue(second.Reused); Assert.AreEqual(first.Instance, second.Instance);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => InstanceUpdateReconnection.AdoptInspectedAsync(restarted,
                peer.InstanceId, operation, "another-old-epoch", proof, default));
        }
        finally { Directory.Delete(state, true); }
    }
    private static string Endpoint(string name) => NativeIpcEndpoint.FromSocketPath(Path.Combine(Path.GetTempPath(), "reconnect-" + name + ".sock"));
}
