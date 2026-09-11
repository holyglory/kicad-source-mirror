using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignElectricalRecoveryTests
{
    private static async Task Isolated(Func<Fixture, Task> test)
    {
        string root = Directory.CreateTempSubdirectory("electrical-recovery-").FullName;
        try { await test(new(root)); }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task LegacyAndElectricalCheckpointsRoundTripWithoutChurn() => Isolated(f =>
    {
        Assert.IsNull(f.Saved.State.BaselineElectrical); Assert.IsNull(f.Saved.State.ObservedElectrical);
        Assert.AreEqual(1, JsonNode.Parse(File.ReadAllText(f.Path))!["Version"]!.GetValue<int>());
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Save(f.Store.Read()!.State, f.Saved.RevisionToken).RevisionToken);
        var state = f.Saved.State with { BaselineElectrical = f.Peer.Electrical.Clone(), ObservedElectrical = f.Peer.Electrical.Clone() };
        var saved = f.Store.Save(state, f.Saved.RevisionToken);
        Assert.AreEqual(3, JsonNode.Parse(File.ReadAllText(f.Path))!["Version"]!.GetValue<int>());
        Assert.AreEqual(state.BaselineElectrical, f.Store.Read()!.State.BaselineElectrical);
        Assert.AreEqual(state.ObservedElectrical, f.Store.Read()!.State.ObservedElectrical);
        Assert.AreEqual(saved.RevisionToken, f.Store.Save(f.Store.Read()!.State, saved.RevisionToken).RevisionToken);
        CollectionAssert.AreEqual(f.Saved.State.DesiredFileBytes, f.Store.Read()!.State.DesiredFileBytes);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task MismatchedAndUnknownCheckpointFieldsCannotReplaceRecovery() => Isolated(f =>
    {
        var valid = f.Saved.State with { BaselineElectrical = f.Peer.Electrical.Clone(), ObservedElectrical = f.Peer.Electrical.Clone() };
        foreach (string kind in new[] { "hierarchy", "revision", "tracking", "future", "baseline-future" })
        {
            var state = valid with { BaselineElectrical = valid.BaselineElectrical!.Clone(), ObservedElectrical = valid.ObservedElectrical!.Clone() };
            if (kind == "hierarchy") state.ObservedElectrical!.Hierarchy.Data.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
            else if (kind == "revision") state.ObservedElectrical!.Hierarchy.Revision.Sequence++;
            else if (kind == "tracking") state.ObservedElectrical!.Hierarchy.TrackingComplete = true;
            else if (kind == "baseline-future") state.BaselineElectrical!.Hierarchy.Revision.Sequence++;
            else state.ObservedElectrical!.MergeFrom(new byte[] { 0x98, 0x06, 0x01 }); // unknown field99
            Assert.ThrowsExactly<AutomationException>(() => f.Store.Save(state, f.Saved.RevisionToken), kind);
            Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ElectricalEnvelopeRejectsDuplicateNestedFieldsAndWrongVersion() => Isolated(f =>
    {
        f.Store.Save(f.Saved.State with { ObservedElectrical = f.Peer.Electrical }, f.Saved.RevisionToken);
        string original = File.ReadAllText(f.Path);
        string duplicated = original.Replace("\"Epoch\":\"electrical-epoch\"", "\"Epoch\":\"electrical-epoch\",\"Epoch\":\"electrical-epoch\"", StringComparison.Ordinal);
        Assert.AreNotEqual(original, duplicated);
        File.WriteAllText(f.Path, duplicated);
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Read());
        var node = JsonNode.Parse(original)!; node["Version"] = 1;
        File.WriteAllText(f.Path, node.ToJsonString());
        Assert.ThrowsExactly<AutomationException>(() => f.Store.Read());
        File.WriteAllText(f.Path, original);
        Assert.IsNotNull(f.Store.Read()!.State.ObservedElectrical);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task InitializeRequiresModelAndNativeAgreementAndPreservesBothBaselines() => Isolated(async f =>
    {
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken);
        Assert.AreEqual(f.Peer.Electrical, initialized.State.BaselineElectrical);
        Assert.AreEqual(initialized.State.BaselineElectrical, initialized.State.ObservedElectrical);
        Assert.AreEqual(f.BaselineXml, SchematicDesignXml.Write(initialized.State.Baseline, initialized.State.KnowledgeLibraries));
        CollectionAssert.AreEqual(f.Saved.State.DesiredFileBytes, initialized.State.DesiredFileBytes);
        Assert.AreEqual("electrical_baseline_exists", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, initialized.RevisionToken))).Code);
        f.Peer.Electrical.Nets.Clear(); f.Peer.Electrical.Hierarchy.Revision.Sequence++;
        var refreshed = await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, initialized.RevisionToken, includeElectrical: true);
        Assert.IsEmpty(refreshed.State.ObservedElectrical!.Nets);
        Assert.IsNotEmpty(refreshed.State.BaselineElectrical!.Nets);
        Assert.AreEqual(initialized.State.BaselineElectrical, refreshed.State.BaselineElectrical);
        Assert.AreEqual(f.BaselineXml, SchematicDesignXml.Write(refreshed.State.Baseline, refreshed.State.KnowledgeLibraries));
        Assert.AreEqual(refreshed.RevisionToken, (await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, refreshed.RevisionToken, includeElectrical: true)).RevisionToken);
    });

    [TestMethod]
    public Task InitializationNeverHidesPendingOperationsOrWrongConnectivity() => Isolated(async f =>
    {
        f.Peer.Electrical.Nets.Clear();
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        var pending = DesignRecoveryStoreTests.Mutation(f.Saved.State);
        var saved = f.Store.Save(f.Saved.State with { PendingMutation = pending }, f.Saved.RevisionToken);
        int calls = f.Peer.Calls;
        Assert.AreEqual("pending_recovery_requires_reconciliation", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, saved.RevisionToken))).Code);
        Assert.AreEqual(calls, f.Peer.Calls); Assert.AreEqual(pending, f.Store.Read()!.State.PendingMutation);
    });

    [TestMethod]
    public Task InitializationDoesNotReplaceChangedHierarchyOrConcurrentXml() => Isolated(async f =>
    {
        f.Peer.Electrical.Hierarchy.Data.Instances[0].Metadata.TextVariables.Add("changed", "native");
        Assert.AreEqual("electrical_baseline_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        f.Peer.Electrical.Hierarchy.Data = f.Saved.State.Observed.Clone();
        f.Peer.BeforeSnapshot = () => f.Store.Save(f.Saved.State with { DesiredFileBytes = [0xfe] }, f.Saved.RevisionToken);
        Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        CollectionAssert.AreEqual(new byte[] { 0xfe }, f.Store.Read()!.State.DesiredFileBytes);
        Assert.IsNull(f.Store.Read()!.State.BaselineElectrical);
    });

    [TestMethod]
    public Task HierarchyOnlyRefreshInvalidatesStaleElectricalObservationButNotBaseline() => Isolated(async f =>
    {
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken);
        var same = await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, initialized.RevisionToken);
        Assert.AreEqual(initialized.RevisionToken, same.RevisionToken);
        f.Peer.Electrical.Hierarchy.Revision.Sequence++;
        var changed = await DesignRecoveryInspector.RefreshAsync(f.Store, f.Client, same.RevisionToken);
        Assert.IsNull(changed.State.ObservedElectrical);
        Assert.AreEqual(initialized.State.BaselineElectrical, changed.State.BaselineElectrical);
    });

    [TestMethod]
    public Task FailedWriteCancellationAndWrongInstancePreserveOriginalRecovery() => Isolated(async f =>
    {
        using (var held = new FileStream(f.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual("design_recovery_io", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store,
            f.Client, f.Saved.RevisionToken, new(true)));
        f.Peer.Instance = Guid.NewGuid().ToString("D");
        Assert.AreEqual("recovery_instance_mismatch", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            DesignRecoveryInspector.InitializeElectricalBaselineAsync(f.Store, f.Client, f.Saved.RevisionToken))).Code);
        Assert.AreEqual(f.Saved.RevisionToken, f.Store.Read()!.RevisionToken);
    });

    private sealed class Fixture
    {
        public string Path { get; }
        public DesignRecoveryStore Store { get; }
        public StoredDesignRecovery Saved { get; }
        public Peer Peer { get; }
        public NativeClient Client { get; }
        public string BaselineXml { get; }
        public Fixture(string root)
        {
            var f = SchematicElectricalComparisonTests.Fixture();
            Path = System.IO.Path.Combine(root, "recovery.json"); Store = new(Path);
            var instance = Guid.NewGuid(); BaselineXml = SchematicDesignXml.Write(f.Design, [f.Library]);
            Saved = Store.Save(new(Guid.NewGuid(), instance, new(f.State.Hierarchy.Revision.Epoch, f.State.Hierarchy.Revision.Sequence),
                false, f.Design, [0xff, 0x3c], f.State.Hierarchy.Data.Clone(), [f.Library]), null);
            Peer = new(f.State.Clone(), instance.ToString("D"));
            Client = new(Peer, "ipc:///tmp/electrical-recovery.sock", "process-epoch");
        }
    }

    private sealed class Peer(SchematicElectricalState electrical, string instance) : INativeTransport
    {
        public SchematicElectricalState Electrical { get; } = electrical;
        public string Instance { get; set; } = instance;
        public Action? BeforeSnapshot;
        public int Calls;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); ++Calls;
            var message = ApiRequest.Parser.ParseFrom(request).Message; IMessage response;
            if (message.Is(GetAutomationSession.Descriptor)) response = new AutomationSession { ProtocolVersion = 1, InstanceId = Instance, Epoch = "process-epoch" };
            else if (message.Is(ReadSchematicElectricalState.Descriptor)) { BeforeSnapshot?.Invoke(); response = Electrical.Clone(); }
            else if (message.Is(ReadSchematicHierarchyData.Descriptor)) { BeforeSnapshot?.Invoke(); response = Electrical.Hierarchy.Clone(); }
            else throw new AssertFailedException("Recovery must never mutate the native peer.");
            return Task.FromResult(new ApiResponse { Header = new() { KicadToken = "process-epoch" },
                Status = new() { Status = (ApiStatusCode)1 }, Message = Any.Pack(response) }.ToByteArray());
        }
    }
}
