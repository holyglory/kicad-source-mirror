using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicHierarchyTopologyTests
{
    internal static SchematicHierarchyData Fixture()
    {
        KIID Id() => new() { Value = Guid.NewGuid().ToString("D") };
        var rootId = Id(); var childId = Id();
        var document = new DocumentSpecifier { Type = (DocumentType)1, SheetPath = new(),
            Project = new() { Name = "fixture", Path = "/fixture" } };
        document.SheetPath.Path.Add(rootId);
        var root = new SchematicScreenData { Metadata = new() { Document = document.Clone(), ScreenId = rootId.Clone() } };
        var result = new SchematicHierarchyData { Document = document.Clone() }; result.Instances.Add(root);
        var note = new SchematicText { Id = Id(), Text = new() { Text_ = "Shared child contents" } };
        foreach (string name in new[] { "A", "B" })
        {
            var sheet = new SheetSymbol { Id = Id(), ChildScreenId = childId.Clone(), Path = document.SheetPath.Clone(),
                NameField = new() { Text = new() { Text_ = name, Attributes = new() { Multiline = true } } },
                FilenameField = new() { Text = new() { Text_ = "child.kicad_sch", Attributes = new() { Multiline = true } } } };
            root.Items.Add(Any.Pack(sheet));
            var target = document.Clone(); target.SheetPath.Path.Add(sheet.Id.Clone());
            var child = new SchematicScreenData { Metadata = new() { Document = target, ScreenId = childId.Clone() } };
            child.Metadata.UnrepresentedState.Add("library_cache"); child.Items.Add(Any.Pack(note)); result.Instances.Add(child);
        }
        return result;
    }

    [TestMethod]
    public void RepeatedScreensRetainSeparateInstancesAndExplicitCoverageGaps()
    {
        var data = Fixture(); string xml = SchematicDataXml.Write(data);
        var report = SchematicHierarchyTopology.Inspect(data);
        Assert.IsTrue(report.IsValid); Assert.AreEqual(3, report.InstanceCount); Assert.AreEqual(2, report.ScreenCount);
        Assert.AreEqual(2, report.CoverageGaps.Count);
        Assert.AreEqual(xml, SchematicDataXml.Write(data));
        var reordered = data.Clone(); reordered.Instances.Clear(); reordered.Instances.Add(data.Instances.Reverse());
        Assert.IsTrue(SchematicHierarchyTopology.Inspect(reordered).IsValid);
    }

    [TestMethod]
    public void RootFileIdentityIsIndependentOfItsProjectInstance()
    {
        var data = Fixture();
        string instanceId = data.Document.SheetPath.Path[0].Value;
        string screenId = Guid.NewGuid().ToString("D");
        data.Instances[0].Metadata.ScreenId.Value = screenId;
        string xml = SchematicDataXml.Write(data);
        var restored = (SchematicHierarchyData)SchematicDataXml.Read(xml);
        Assert.IsTrue(SchematicHierarchyTopology.Inspect(restored).IsValid,
            "A project root instance and the native file it references need not have the same UUID.");
        Assert.AreEqual(instanceId, restored.Document.SheetPath.Path[0].Value);
        Assert.AreEqual(screenId, restored.Instances[0].Metadata.ScreenId.Value);
        Assert.IsEmpty(SchematicHierarchyDelta.Plan(data, restored));
        Assert.AreEqual(xml, SchematicDataXml.Write(data));

        var childLink = restored.Instances[0].Items[0].Unpack<SheetSymbol>();
        childLink.ChildScreenId = restored.Instances[0].Metadata.ScreenId.Clone();
        restored.Instances[0].Items[0] = Any.Pack(childLink);
        restored.Instances[1].Metadata.ScreenId = childLink.ChildScreenId.Clone();
        Assert.IsTrue(SchematicHierarchyTopology.Inspect(restored).Issues.Any(i => i.Code == "recursive_screen"),
            "Recursion must still be checked using physical screen identities.");
    }

    [TestMethod]
    public void MissingConflictingAndRecursiveReferencesAreReportedWithoutChangingInput()
    {
        foreach (string problem in new[] { "missing_child", "duplicate_instance", "wrong_child_screen", "wrong_parent_path",
                     "recursive_screen", "conflicting_shared_topology", "different_document", "missing_root", "invalid_sheet_reference" })
        {
            var data = Fixture(); var sheet = data.Instances[0].Items[0].Unpack<SheetSymbol>();
            switch (problem)
            {
                case "missing_child": data.Instances.RemoveAt(1); break;
                case "duplicate_instance": data.Instances.Add(data.Instances[1].Clone()); break;
                case "wrong_child_screen": data.Instances[1].Metadata.ScreenId.Value = Guid.NewGuid().ToString("D"); break;
                case "wrong_parent_path": sheet.Path.Path[0].Value = Guid.NewGuid().ToString("D"); data.Instances[0].Items[0] = Any.Pack(sheet); break;
                case "recursive_screen":
                    data.Instances[1].Metadata.ScreenId = data.Instances[0].Metadata.ScreenId.Clone();
                    sheet.ChildScreenId = data.Instances[0].Metadata.ScreenId.Clone(); data.Instances[0].Items[0] = Any.Pack(sheet); break;
                case "conflicting_shared_topology":
                    sheet.Id.Value = Guid.NewGuid().ToString("D"); sheet.Path = data.Instances[1].Metadata.Document.SheetPath.Clone();
                    data.Instances[1].Items.Add(Any.Pack(sheet)); break;
                case "different_document": data.Instances[1].Metadata.Document.Project.Name = "other"; break;
                case "missing_root": data.Instances.RemoveAt(0); break;
                case "invalid_sheet_reference": data.Instances[0].Items.Add(Any.Pack(sheet)); break;
            }
            string xml = SchematicDataXml.Write(data);
            var report = SchematicHierarchyTopology.Inspect(data);
            Assert.IsFalse(report.IsValid, problem);
            Assert.IsTrue(report.Issues.Any(i => i.Code == problem), problem);
            Assert.AreEqual(xml, SchematicDataXml.Write(data));
        }
    }

    [TestMethod]
    public void CancellationAndInvalidIdentityNeverProduceAValidReport()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicHierarchyTopology.Inspect(Fixture(), cancellation.Token));
        var data = Fixture(); data.Instances[1].Metadata.ScreenId.Value = "invalid";
        Assert.IsFalse(SchematicHierarchyTopology.Inspect(data).IsValid);
        data = Fixture(); data.Document.SheetPath.Path.Clear();
        Assert.IsFalse(SchematicHierarchyTopology.Inspect(data).IsValid);
    }
}
