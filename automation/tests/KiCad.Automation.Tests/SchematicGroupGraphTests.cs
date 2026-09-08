using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicGroupGraphTests
{
    private static KIID Id() => new() { Value = Guid.NewGuid().ToString("D") };
    private static SchematicScreenData Fixture()
    {
        var screen = Id(); var note = new SchematicText { Id = Id(), Text = new() { Text_ = "Supply" } };
        var inner = new Group { Id = Id(), Name = "Inner", DesignBlockLibraryId = "FixtureBlocks:Supply" };
        inner.Items.Add(note.Id.Clone());
        var outer = new Group { Id = Id(), Name = "Outer" }; outer.Items.Add(inner.Id.Clone());
        var data = new SchematicScreenData { Metadata = new() { ScreenId = screen, Document = new() { SheetPath = new() } } };
        data.Metadata.Document.SheetPath.Path.Add(screen.Clone());
        data.Items.Add(Any.Pack(note)); data.Items.Add(Any.Pack(inner)); data.Items.Add(Any.Pack(outer));
        return data;
    }

    [TestMethod]
    public void NestedGroupsPreserveLibraryLinksAndAllowIndependentMemberEdits()
    {
        var baseline = Fixture(); var saved = SchematicDataXml.Write(baseline);
        var desired = (SchematicScreenData)SchematicDataXml.Read(saved);
        Assert.AreEqual("FixtureBlocks:Supply", desired.Items[1].Unpack<Group>().DesignBlockLibraryId);
        Assert.AreEqual(0, SchematicItemDelta.Plan(baseline, desired).Count);
        var note = desired.Items[0].Unpack<SchematicText>(); note.Text.Text_ = "Updated supply note";
        desired.Items[0] = Any.Pack(note);
        Assert.AreEqual(1, SchematicItemDelta.Plan(baseline, desired).Count);
        Assert.AreEqual(saved, SchematicDataXml.Write(baseline));
        var changedGroup = desired.Items[1].Unpack<Group>(); changedGroup.Name = "Changed";
        desired.Items[1] = Any.Pack(changedGroup);
        Assert.AreEqual(2, SchematicItemDelta.Plan(baseline, desired).Count);
        changedGroup.Items.Clear(); desired.Items[1] = Any.Pack(changedGroup);
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(baseline, desired));
    }

    [TestMethod]
    public void MembershipChangesDetachBeforeCreatingAndAttachingMembers()
    {
        var baseline = Fixture(); var desired = baseline.Clone();
        var previousNote = desired.Items[0].Unpack<SchematicText>();
        var newNote = previousNote.Clone(); newNote.Id = Id();
        var inner = desired.Items[1].Unpack<Group>(); inner.Items.Clear(); inner.Items.Add(newNote.Id.Clone());
        var outer = desired.Items[2].Unpack<Group>(); outer.Items.Add(previousNote.Id.Clone());
        desired.Items[1] = Any.Pack(inner); desired.Items[2] = Any.Pack(outer); desired.Items.Add(Any.Pack(newNote));
        var plan = SchematicItemDelta.Plan(baseline, desired);
        Assert.AreEqual(4, plan.Count);
        Assert.AreEqual(0, plan[0].Update.Unpack<Group>().Items.Count);
        Assert.AreEqual(newNote, plan[1].Create.Unpack<SchematicText>());
        Assert.AreEqual(inner, plan[2].Update.Unpack<Group>());
        Assert.AreEqual(outer, plan[3].Update.Unpack<Group>());
        Assert.AreEqual(0, SchematicItemDelta.Plan(desired, desired).Count);
    }

    [TestMethod]
    public void DeletingAGroupCanTransferSurvivorsToAnExistingOrNewGroup()
    {
        var baseline = Fixture();
        foreach (bool newParent in new[] { false, true })
        {
            var desired = baseline.Clone();
            var outer = desired.Items[2].Unpack<Group>();
            outer.Items.Clear(); outer.Items.Add(desired.Items[0].Unpack<SchematicText>().Id.Clone());
            if (newParent) outer.Id = Id();
            desired.Items.RemoveAt(2); desired.Items.RemoveAt(1); desired.Items.Add(Any.Pack(outer));
            var plan = SchematicItemDelta.Plan(baseline, desired);
            Assert.IsTrue(plan.Any(op => op.Remove?.Equals(baseline.Items[1].Unpack<Group>().Id) == true));
            Assert.AreEqual(outer, newParent ? plan[^1].Create.Unpack<Group>() : plan[^1].Update.Unpack<Group>());
            Assert.IsFalse(plan.Any(op => op.Remove?.Equals(baseline.Items[0].Unpack<SchematicText>().Id) == true));
        }
    }

    [TestMethod]
    public void GroupRemovalOrdersParentsFirstAndPreservesMembers()
    {
        var baseline = Fixture(); var desired = baseline.Clone();
        desired.Items.RemoveAt(2); desired.Items.RemoveAt(1);
        var plan = SchematicItemDelta.Plan(baseline, desired);
        Assert.AreEqual(2, plan.Count);
        Assert.AreEqual(baseline.Items[2].Unpack<Group>().Id, plan[0].Remove);
        Assert.AreEqual(baseline.Items[1].Unpack<Group>().Id, plan[1].Remove);
        desired.Items.Clear(); plan = SchematicItemDelta.Plan(baseline, desired);
        Assert.AreEqual(baseline.Items[0].Unpack<SchematicText>().Id, plan[2].Remove);
    }

    [TestMethod]
    public void NewNestedGroupsFollowTheirMembersRegardlessOfIdentityOrder()
    {
        var desired = Fixture(); var baseline = desired.Clone(); baseline.Items.Clear();
        var plan = SchematicItemDelta.Plan(baseline, desired);
        Assert.AreEqual(3, plan.Count);
        Assert.IsTrue(plan[0].Create.Is(SchematicText.Descriptor));
        Assert.AreEqual("Inner", plan[1].Create.Unpack<Group>().Name);
        Assert.AreEqual("Outer", plan[2].Create.Unpack<Group>().Name);
        var empty = desired.Items[2].Unpack<Group>(); empty.Items.Clear(); desired.Items[2] = Any.Pack(empty);
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(baseline, desired));
    }

    [TestMethod]
    public void InvalidMembershipRejectsEvenUnchangedSnapshotsAndKeepsInput()
    {
        foreach (string problem in new[] { "missing", "duplicate", "self", "cycle", "two_parents", "noncanonical" })
        {
            var data = Fixture(); var inner = data.Items[1].Unpack<Group>(); var outer = data.Items[2].Unpack<Group>();
            switch (problem)
            {
                case "missing": inner.Items.Add(Id()); break;
                case "duplicate": inner.Items.Add(inner.Items[0].Clone()); break;
                case "self": inner.Items.Add(inner.Id.Clone()); break;
                case "cycle": inner.Items.Add(outer.Id.Clone()); break;
                case "two_parents": outer.Items.Add(inner.Items[0].Clone()); break;
                case "noncanonical": inner.Items[0].Value = "not-a-uuid"; break;
            }
            data.Items[1] = Any.Pack(inner); data.Items[2] = Any.Pack(outer);
            var original = SchematicDataXml.Write(data);
            var error = Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(data, data), problem);
            Assert.AreEqual("unsupported_schematic_delta", error.Code);
            Assert.AreEqual(original, SchematicDataXml.Write(data));
        }
    }

    [TestMethod]
    public void RemovingAMemberWithoutUpdatingItsGroupCannotProduceAPartialPlan()
    {
        var baseline = Fixture(); var desired = baseline.Clone(); desired.Items.RemoveAt(0);
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(baseline, desired));
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(baseline, desired, baseline));
    }
}
