using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNetChainExclusionTests
{
    private static SchematicHierarchyData Fixture()
    {
        var value = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in value.Instances) screen.Metadata.NetChains.Add(new SchematicNetChainDefinition
        {
            Name = "chain", From = new() { Reference = "TP1", Pin = "1" }, To = new() { Reference = "TP2", Pin = "1" },
            MemberNets = { "/A", "/B" }, Exclusions = new()
        });
        return value;
    }

    [TestMethod]
    public void ExclusionsRoundTripAndPlanOnceWithoutDiscardingEndpointIntent()
    {
        var before = Fixture(); var after = before.Clone();
        foreach (var sheet in after.Instances)
        { sheet.Metadata.NetChains[0].MemberNets.Remove("/A"); sheet.Metadata.NetChains[0].Exclusions.NetNames.Add("/A"); }
        string xml = SchematicDataXml.Write(after);
        Assert.AreEqual(after, SchematicDataXml.Read(xml));
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        var chain = operations.Single().ReplaceNetChains.Definitions.Single();
        CollectionAssert.AreEqual(new[] { "/A" }, chain.Exclusions.NetNames.ToArray());
        Assert.AreEqual(before.Instances[0].Metadata.NetChains[0].From, chain.From);
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(after, after).Count);
        var legacy = after.Clone(); foreach (var sheet in legacy.Instances) sheet.Metadata.NetChains[0].Exclusions = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(after, legacy));
        Assert.AreEqual(1, SchematicHierarchyDelta.Plan(after, before).Count, "Explicit empty exclusions can restore membership.");
    }

    [TestMethod]
    public void MalformedOrContradictoryExclusionsFailBeforeNativeMutation()
    {
        var before = Fixture();
        foreach (string[] names in new[] { new[] { "/A" }, new[] { "/C", "/C" }, new[] { "" }, new[] { "bad\0net" }, new[] { "__SG_runtime" } })
        {
            var after = before.Clone();
            foreach (var sheet in after.Instances) sheet.Metadata.NetChains[0].Exclusions.NetNames.Add(names);
            Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        }
    }

    [TestMethod]
    public void ConcurrentRemovalAndRetargetingRemainAnExplicitConflict()
    {
        var before = Fixture().Instances[0]; var xml = before.Clone(); var native = before.Clone();
        xml.Metadata.NetChains[0].MemberNets.Remove("/A"); xml.Metadata.NetChains[0].Exclusions.NetNames.Add("/A");
        native.Metadata.NetChains[0].From.Reference = "TP3";
        var result = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsFalse(result.CanApply);
        Assert.AreEqual(1, result.Conflicts.Count);
        native = before.Clone(); native.Metadata.TextVariables["keep"] = "untouched";
        result = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual("untouched", result.Merged!.Metadata.TextVariables["keep"]);
        CollectionAssert.AreEqual(new[] { "/A" }, result.Merged.Metadata.NetChains[0].Exclusions.NetNames.ToArray());
    }
}
