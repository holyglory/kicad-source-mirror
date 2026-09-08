using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicSyncStoreTests
{
    [TestMethod]
    public void CacheChoicesPersistWithoutDiscardingObjectConflictsOrOriginalVersions() => Isolated(path =>
    {
        var input = Fixture();
        foreach (var state in new[] { input.Baseline, input.Xml, input.Native })
            state.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Lib:Part", Definition = new() });
        input.Xml.CachedSymbols[0].ShowPinNames = true;
        input.Native.CachedSymbols[0].ShowPinNumbers = true;
        var store = new SchematicSyncStore(path); var saved = store.Save(input, null);
        var partial = store.Resolve(saved.RevisionToken, new Dictionary<Guid, SchematicConflictChoice>(),
            cacheChoices: new Dictionary<string, SchematicConflictChoice> { ["Lib:Part"] = SchematicConflictChoice.Xml });
        Assert.IsFalse(SchematicSyncStore.Plan(partial.State).CanApply);
        Assert.AreEqual(input.Xml, partial.State.Xml);
        Assert.AreEqual(input.Native, partial.State.Native);
        Assert.AreEqual(input.Baseline, partial.State.Baseline);
        var id = Guid.Parse(input.Xml.Items[0].Unpack<SchematicText>().Id.Value);
        var resolved = new SchematicSyncStore(path).Resolve(partial.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Native });
        Assert.IsTrue(SchematicSyncStore.Plan(resolved.State).CanApply);
        Assert.AreEqual(SchematicConflictChoice.Xml, resolved.State.Resolution!.CacheChoices!["Lib:Part"]);
        Assert.AreEqual(resolved.RevisionToken, store.Save(resolved.State, resolved.RevisionToken).RevisionToken);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void VariantAndObjectChoicesPersistIndependentlyWithoutReplacingVersions(bool variantFirst) => Isolated(path =>
    {
        var input = Fixture();
        input.Baseline.Metadata.VariantDescriptions.Add("Assembly", "original");
        input.Xml.Metadata.VariantDescriptions.Add("Assembly", "XML");
        input.Native.Metadata.VariantDescriptions.Add("Assembly", "native");
        var store = new SchematicSyncStore(path); var saved = store.Save(input, null);
        var id = Guid.Parse(input.Xml.Items[0].Unpack<SchematicText>().Id.Value);
        var objectChoices = new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Native };
        var variantChoices = new Dictionary<string, SchematicConflictChoice> { ["Assembly"] = SchematicConflictChoice.Xml };
        var partial = variantFirst
            ? store.Resolve(saved.RevisionToken, new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: variantChoices)
            : store.Resolve(saved.RevisionToken, objectChoices);
        Assert.IsFalse(SchematicSyncStore.Plan(partial.State).CanApply);
        var complete = variantFirst ? store.Resolve(partial.RevisionToken, objectChoices)
            : store.Resolve(partial.RevisionToken, new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: variantChoices);
        var reopened = new SchematicSyncStore(path).Read()!;
        Assert.AreEqual(complete.RevisionToken, reopened.RevisionToken);
        Assert.AreEqual(input.Baseline, reopened.State.Baseline);
        Assert.AreEqual(input.Xml, reopened.State.Xml);
        Assert.AreEqual(input.Native, reopened.State.Native);
        var plan = SchematicSyncStore.Plan(reopened.State);
        Assert.IsTrue(plan.CanApply);
        Assert.AreEqual("XML", plan.Merged!.Metadata.VariantDescriptions["Assembly"]);
        Assert.AreEqual(input.Native.Items[0], plan.Merged.Items[0]);
        Assert.AreEqual(reopened.RevisionToken, store.Resolve(reopened.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice>(), variantChoices: new Dictionary<string, SchematicConflictChoice>
                { ["Assembly"] = SchematicConflictChoice.Xml }).RevisionToken);
        Assert.AreEqual("sync_store_changed", Assert.ThrowsExactly<AutomationException>(() =>
            store.Resolve(saved.RevisionToken, new Dictionary<Guid, SchematicConflictChoice>(),
                variantChoices: new Dictionary<string, SchematicConflictChoice> { ["Assembly"] = SchematicConflictChoice.Native })).Code);
    });

    private static SchematicSyncState Fixture()
    {
        var id = new Kiapi.Common.Types.KIID { Value = Guid.NewGuid().ToString("D") };
        var baseline = new SchematicScreenData { Metadata = new() { ScreenId = id, Document = new() { SheetPath = new() } } };
        baseline.Metadata.Document.SheetPath.Path.Add(id.Clone());
        var note = new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "original" } };
        baseline.Items.Add(Any.Pack(note));
        var xml = baseline.Clone(); note.Text.Text_ = "XML choice"; xml.Items[0] = Any.Pack(note);
        var native = baseline.Clone(); note.Text.Text_ = "native choice"; native.Items[0] = Any.Pack(note);
        return new(Guid.NewGuid(), new("native-session", 12), false, baseline, xml, native);
    }
    private static void Isolated(Action<string> test)
    {
        string root = Directory.CreateTempSubdirectory("kicad-sync-store-").FullName;
        try { test(Path.Combine(root, "checkpoint.json")); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ReopeningPreservesEveryConflictVersionAndSessionIdentity() => Isolated(path =>
    {
        var input = Fixture(); var store = new SchematicSyncStore(path);
        Assert.IsNull(store.Read());
        var saved = store.Save(input, null);
        var reopened = new SchematicSyncStore(path).Read(); Assert.IsNotNull(reopened);
        Assert.AreEqual(saved.RevisionToken, reopened.RevisionToken);
        Assert.AreEqual(input, reopened.State);
        var conflict = SchematicItemMerge.Plan(reopened.State.Baseline, reopened.State.Xml, reopened.State.Native).Conflicts.Single();
        Assert.AreEqual("competing_edit", conflict.Reason);
        Assert.AreEqual(input.Xml.Items[0], conflict.Xml); Assert.AreEqual(input.Native.Items[0], conflict.Native);
        var unchanged = store.Save(input, saved.RevisionToken);
        Assert.AreEqual(saved.RevisionToken, unchanged.RevisionToken);
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
    });

    [TestMethod]
    public void ResolutionSurvivesReopenWithoutReplacingOriginalVersions() => Isolated(path =>
    {
        var input = Fixture(); var store = new SchematicSyncStore(path);
        var saved = store.Save(input, null);
        var id = Guid.Parse(input.Xml.Items[0].Unpack<SchematicText>().Id.Value);
        var resolved = store.Resolve(saved.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Native });
        var reopened = new SchematicSyncStore(path).Read()!;
        Assert.AreEqual(resolved.RevisionToken, reopened.RevisionToken);
        Assert.AreEqual(input.Xml, reopened.State.Xml);
        Assert.AreEqual(input.Native, reopened.State.Native);
        Assert.AreEqual(input.Baseline, reopened.State.Baseline);
        var plan = SchematicSyncStore.Plan(reopened.State);
        Assert.IsTrue(plan.CanApply); Assert.AreEqual(0, plan.NativeOperations.Count);
        Assert.AreEqual(input.Native, plan.Merged);
        Assert.AreEqual(resolved.RevisionToken, store.Resolve(resolved.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Native }).RevisionToken);
        Assert.AreEqual("sync_store_changed", Assert.ThrowsExactly<AutomationException>(() =>
            store.Resolve(saved.RevisionToken, new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Xml })).Code);
        Assert.AreEqual(resolved.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public void ChainChoicesPersistWithOriginalVersionsAndRejectStaleSessions() => Isolated(path =>
    {
        var input = Fixture();
        input.Xml.Items.Clear(); input.Xml.Items.Add(input.Baseline.Items.Select(i => i.Clone()));
        input.Native.Items.Clear(); input.Native.Items.Add(input.Baseline.Items.Select(i => i.Clone()));
        input.Baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Original" });
        input.Xml.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Xml" });
        input.Native.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Native" });
        var store = new SchematicSyncStore(path); var saved = store.Save(input, null);
        var choices = new Dictionary<string, SchematicConflictChoice> { ["PATH"] = SchematicConflictChoice.Xml };
        var resolved = store.Resolve(saved.RevisionToken, new Dictionary<Guid, SchematicConflictChoice>(), choices);
        var reopened = new SchematicSyncStore(path).Read()!;
        Assert.AreEqual(input.Xml, reopened.State.Xml); Assert.AreEqual(input.Native, reopened.State.Native);
        Assert.AreEqual(input.Baseline, reopened.State.Baseline);
        var plan = SchematicSyncStore.Plan(reopened.State);
        Assert.IsTrue(plan.CanApply); Assert.AreEqual("Xml", plan.Merged!.Metadata.NetChains.Single().NetClass);
        Assert.AreEqual(resolved.RevisionToken, store.Resolve(resolved.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice>(), choices).RevisionToken);
        Assert.AreEqual("stale_sync_resolution", Assert.ThrowsExactly<AutomationException>(() =>
            SchematicSyncStore.Plan(reopened.State with { NativeRevision = new("restarted", 0) })).Code);
        Assert.AreEqual(resolved.RevisionToken, store.Read()!.RevisionToken);
    });

    [TestMethod]
    public void PartialObjectChoicesPersistWhileChainConflictRemains() => Isolated(path =>
    {
        var input = Fixture();
        input.Baseline.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Original" });
        input.Xml.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Xml" });
        input.Native.Metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "PATH", NetClass = "Native" });
        var store = new SchematicSyncStore(path); var saved = store.Save(input, null);
        var id = Guid.Parse(input.Xml.Items[0].Unpack<SchematicText>().Id.Value);
        var partial = store.Resolve(saved.RevisionToken, new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Xml });
        Assert.IsFalse(SchematicSyncStore.Plan(new SchematicSyncStore(path).Read()!.State).CanApply);
        var complete = store.Resolve(partial.RevisionToken, new Dictionary<Guid, SchematicConflictChoice>(),
            new Dictionary<string, SchematicConflictChoice> { ["PATH"] = SchematicConflictChoice.Native });
        Assert.IsTrue(SchematicSyncStore.Plan(complete.State).CanApply);
        Assert.AreEqual(input.Xml, complete.State.Xml); Assert.AreEqual(input.Native, complete.State.Native);
        Assert.AreEqual(SchematicConflictChoice.Xml, complete.State.Resolution!.Choices[id]);
    });

    [TestMethod]
    public void ChoicesCannotSilentlyFollowChangedContentOrRestartedSession() => Isolated(path =>
    {
        var input = Fixture(); var store = new SchematicSyncStore(path);
        var saved = store.Save(input, null);
        var id = Guid.Parse(input.Xml.Items[0].Unpack<SchematicText>().Id.Value);
        var resolved = store.Resolve(saved.RevisionToken,
            new Dictionary<Guid, SchematicConflictChoice> { [id] = SchematicConflictChoice.Xml });
        var edited = resolved.State.Xml.Clone();
        var note = edited.Items[0].Unpack<SchematicText>(); note.Text.Text_ = "new edit";
        edited.Items[0] = Any.Pack(note);
        foreach (var stale in new[] { resolved.State with { Xml = edited },
            resolved.State with { NativeRevision = new("restarted", 0) } })
        {
            Assert.AreEqual("stale_sync_resolution", Assert.ThrowsExactly<AutomationException>(() => SchematicSyncStore.Plan(stale)).Code);
            Assert.AreEqual("stale_sync_resolution", Assert.ThrowsExactly<AutomationException>(() => store.Save(stale, resolved.RevisionToken)).Code);
        }
        Assert.AreEqual(resolved.RevisionToken, store.Read()!.RevisionToken);
        var fresh = store.Save(resolved.State with { Xml = edited, Resolution = null }, resolved.RevisionToken);
        Assert.IsFalse(SchematicSyncStore.Plan(fresh.State).CanApply);
    });

    [TestMethod]
    public void StaleWritersCannotReplaceAnAcceptedCheckpoint() => Isolated(path =>
    {
        var input = Fixture(); var first = new SchematicSyncStore(path); var second = new SchematicSyncStore(path);
        var saved = first.Save(input, null);
        var next = second.Save(input with { NativeRevision = new("new-session", 0) }, saved.RevisionToken);
        Assert.AreEqual("sync_store_changed", Assert.ThrowsExactly<AutomationException>(() => first.Save(input, saved.RevisionToken)).Code);
        Assert.AreEqual("sync_store_changed", Assert.ThrowsExactly<AutomationException>(() => first.Save(input, null)).Code);
        Assert.AreEqual(next.State, first.Read()!.State);
    });

    [TestMethod]
    public void WriterOwnershipFailurePreservesLastRecordAndRecovers() => Isolated(path =>
    {
        var input = Fixture(); var store = new SchematicSyncStore(path); var saved = store.Save(input, null);
        byte[] original = File.ReadAllBytes(path);
        using (var other = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual("sync_store_io", Assert.ThrowsExactly<AutomationException>(() => store.Save(input with { OriginId = Guid.NewGuid() }, saved.RevisionToken)).Code);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsNotNull(store.Save(input with { OriginId = Guid.NewGuid() }, saved.RevisionToken));
    });

    [TestMethod]
    public void CorruptFutureMissingAndDuplicateFieldsAreNotOverwritten() => Isolated(path =>
    {
        var input = Fixture(); var store = new SchematicSyncStore(path); store.Save(input, null);
        string valid = File.ReadAllText(path);
        var missing = JsonNode.Parse(valid)!.AsObject(); missing.Remove("NativeSequence");
        foreach (string corrupt in new[] { "{broken", valid.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal),
            missing.ToJsonString(), valid.Insert(1, "\"Version\":1,") })
        {
            File.WriteAllText(path, corrupt);
            Assert.AreEqual("invalid_sync_store", Assert.ThrowsExactly<AutomationException>(() => store.Read()).Code);
            Assert.ThrowsExactly<AutomationException>(() => store.Save(input, null));
            Assert.AreEqual(corrupt, File.ReadAllText(path));
        }
    });

    [TestMethod]
    public void InvalidTargetsAndIoFailureDoNotCreateSuccessfulState() => Isolated(path =>
    {
        var input = Fixture(); var store = new SchematicSyncStore(path);
        var wrong = input.Xml.Clone(); wrong.Metadata.ScreenId.Value = Guid.NewGuid().ToString("D");
        Assert.AreEqual("invalid_sync_store", Assert.ThrowsExactly<AutomationException>(() => store.Save(input with { Xml = wrong }, null)).Code);
        Assert.IsFalse(File.Exists(path));
        Directory.CreateDirectory(path);
        Assert.AreEqual("sync_store_io", Assert.ThrowsExactly<AutomationException>(() => store.Save(input, null)).Code);
        Assert.IsTrue(Directory.Exists(path));
    });
}
