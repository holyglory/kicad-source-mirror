using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicDrawingRatiosTests
{
    private static SchematicDrawingRatios Ratios() => new()
    {
        DashLengthRatio = 12, GapLengthRatio = 3, TextOffsetRatio = 0.15,
        LabelSizeRatio = 0.4, OverbarHeightRatio = 1.2
    };

    [TestMethod]
    public void HierarchyDrawingRatiosHaveOneConsistentProjectOwner()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in before.Instances) screen.Metadata.DrawingRatios = Ratios();
        var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.DrawingRatios.GapLengthRatio = 9;
        string xml = SchematicDataXml.Write(after);
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        Assert.AreEqual(9d, operations.Single().SetDrawingRatios.GapLengthRatio);
        Assert.AreEqual(xml, SchematicDataXml.Write(after));
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(after, after).Count);
        after.Instances[^1].Metadata.DrawingRatios.GapLengthRatio = 4;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void DrawingChangesMergeWithNativeNotesButOverlappingRatiosRemainConflicts()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.DrawingRatios = Ratios();
        var xml = baseline.Clone(); xml.Metadata.DrawingRatios.DashLengthRatio = 8;
        var native = baseline.Clone(); native.Metadata.TextVariables.Add("NOTE", "Keep next to connector");
        var result = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual("Keep next to connector", result.Merged!.Metadata.TextVariables["NOTE"]);
        Assert.AreEqual(8d, result.NativeOperations.Single().SetDrawingRatios.DashLengthRatio);
        native.Metadata.DrawingRatios.GapLengthRatio = 10;
        string originalXml = SchematicDataXml.Write(xml), originalNative = SchematicDataXml.Write(native);
        var conflict = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsFalse(conflict.CanApply, "Coupled drawing-ratio edits require explicit conflict resolution.");
        Assert.AreEqual(originalXml, SchematicDataXml.Write(xml));
        Assert.AreEqual(originalNative, SchematicDataXml.Write(native));
    }
}
