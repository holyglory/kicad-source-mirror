using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicFormattingTests
{
    internal static SchematicFormattingSettings Formatting() => new()
    {
        DefaultLineWidthNm = 152400, DefaultTextSizeNm = 1270000,
        PinSymbolSizeNm = 635000, ConnectionGridNm = 1270000,
        JunctionSizeChoice = 3, HopOverSizeChoice = 0, ShowDnpMarkers = true,
        ShowIntersheetReferences = false, ListOwnPage = true,
        ShortReferenceFormat = false, ReferencePrefix = "[", ReferenceSuffix = "]",
        OperatingPoint = new() { VoltagePrecision = 3, CurrentPrecision = 3, VoltageRange = "~V", CurrentRange = "~A" },
        UnitReference = new() { SeparatorAscii = 0, FirstIdAscii = 'A' }
    };

    [TestMethod]
    public void FormattingRoundTripsAndProducesOneProjectOperationAcrossRepeatedSheets()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        foreach (var screen in before.Instances) screen.Metadata.Formatting = Formatting();
        var after = before.Clone();
        foreach (var screen in after.Instances)
        {
            var settings = screen.Metadata.Formatting;
            settings.DefaultLineWidthNm = 254000; settings.DefaultTextSizeNm = 1524000;
            settings.PinSymbolSizeNm = 0; settings.ConnectionGridNm = 635000;
            settings.JunctionSizeChoice = 0; settings.HopOverSizeChoice = 5;
            settings.ShowDnpMarkers = false; settings.ShowIntersheetReferences = true;
            settings.ListOwnPage = false; settings.ShortReferenceFormat = true;
            settings.ReferencePrefix = "頁〈"; settings.ReferenceSuffix = "〉";
            settings.OperatingPoint.VoltagePrecision = 10; settings.OperatingPoint.CurrentPrecision = 1;
            settings.OperatingPoint.VoltageRange = "mV"; settings.OperatingPoint.CurrentRange = "uA";
            settings.UnitReference.SeparatorAscii = '.'; settings.UnitReference.FirstIdAscii = '1';
        }
        var xml = SchematicDataXml.Write(after);
        Assert.AreEqual(after, SchematicDataXml.Read(xml));
        var operation = SchematicHierarchyDelta.Plan(before, after).Single();
        Assert.AreEqual(after.Instances[0].Metadata.Formatting, operation.SetFormatting);
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(after, after.Clone()).Count);
        after.Instances[^1].Metadata.Formatting.ShowDnpMarkers = true;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void InvalidOrMissingFormattingNeverProducesALossyNativeDelta()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.Formatting = Formatting();
        foreach (Action<SchematicFormattingSettings> corrupt in new Action<SchematicFormattingSettings>[]
        {
            s => s.DefaultLineWidthNm = 126900, s => s.DefaultTextSizeNm = 25400100,
            s => s.PinSymbolSizeNm = -100, s => s.ConnectionGridNm = 634900,
            s => s.ConnectionGridNm = 254000100, s => s.DefaultTextSizeNm += 1,
            s => s.JunctionSizeChoice = 6, s => s.HopOverSizeChoice = uint.MaxValue,
            s => s.ReferencePrefix = "\0", s => s.ReferenceSuffix = "end\0",
            s => s.OperatingPoint = null, s => s.OperatingPoint.VoltagePrecision = 0,
            s => s.OperatingPoint.CurrentPrecision = 11, s => s.OperatingPoint.VoltageRange = "mA",
            s => s.OperatingPoint.CurrentRange = "kA", s => s.OperatingPoint.CurrentRange = " A",
            s => s.OperatingPoint.VoltageRange = "V\0", s => s.UnitReference = null,
            s => s.UnitReference.SeparatorAscii = 127, s => s.UnitReference.FirstIdAscii = 48,
            s => s.UnitReference.FirstIdAscii = 123
        })
        {
            var after = before.Clone(); corrupt(after.Metadata.Formatting);
            Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, after));
        }
        var missing = before.Clone(); missing.Metadata.Formatting = null;
        Assert.ThrowsExactly<AutomationException>(() => SchematicItemDelta.Plan(before, missing));
        Assert.AreEqual(0, SchematicItemDelta.Plan(before, before).Count);
    }

    [TestMethod]
    public void EveryNativeOperatingPointRangeRoundTripsAndCanBePlanned()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.Formatting = Formatting();
        foreach (string prefix in new[] { "~", "f", "p", "n", "u", "m", "", "K", "M", "G", "T", "P" })
        {
            var after = before.Clone();
            after.Metadata.Formatting.OperatingPoint.VoltageRange = prefix + "V";
            after.Metadata.Formatting.OperatingPoint.CurrentRange = prefix + "A";
            Assert.AreEqual(after, SchematicDataXml.Read(SchematicDataXml.Write(after)));
            var operations = SchematicItemDelta.Plan(before, after);
            if (prefix == "~") Assert.AreEqual(0, operations.Count);
            else Assert.AreEqual(after.Metadata.Formatting, operations.Single().SetFormatting);
        }
    }

    [TestMethod]
    public void FormattingMergesWithNativeNotesButCompetingFormattingRemainsExplicit()
    {
        var before = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        before.Metadata.Formatting = Formatting();
        var xml = before.Clone(); xml.Metadata.Formatting.ReferencePrefix = "Page ";
        var native = before.Clone(); native.Metadata.TextVariables.Add("NOTE", "Preserve this placement");
        var merged = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual("Preserve this placement", merged.Merged!.Metadata.TextVariables["NOTE"]);
        Assert.AreEqual("Page ", merged.NativeOperations.Single().SetFormatting.ReferencePrefix);
        native.Metadata.Formatting.ReferencePrefix = "Sheet ";
        var originalXml = SchematicDataXml.Write(xml);
        var originalNative = SchematicDataXml.Write(native);
        var conflict = SchematicItemMerge.Plan(before, xml, native);
        Assert.IsFalse(conflict.CanApply);
        Assert.AreEqual(originalXml, SchematicDataXml.Write(xml));
        Assert.AreEqual(originalNative, SchematicDataXml.Write(native));
    }

    [TestMethod]
    public void IndependentFormattingFieldsMergeAndConvergentChangesAreNoOps()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.Formatting = Formatting();
        var xml = baseline.Clone(); xml.Metadata.Formatting.DefaultTextSizeNm = 1524000;
        var native = baseline.Clone(); native.Metadata.Formatting.ReferencePrefix = "Sheet ";
        var originalXml = xml.Clone(); var originalNative = native.Clone();
        var merged = SchematicItemMerge.Plan(baseline, xml, native);
        Assert.IsTrue(merged.CanApply);
        Assert.AreEqual(1524000L, merged.Merged!.Metadata.Formatting.DefaultTextSizeNm);
        Assert.AreEqual("Sheet ", merged.Merged.Metadata.Formatting.ReferencePrefix);
        Assert.AreEqual(merged.Merged.Metadata.Formatting, merged.NativeOperations.Single().SetFormatting);
        Assert.AreEqual(originalXml, xml); Assert.AreEqual(originalNative, native);
        var converged = SchematicItemMerge.Plan(baseline, merged.Merged, merged.Merged.Clone());
        Assert.IsTrue(converged.CanApply);
        Assert.AreEqual(0, converged.NativeOperations.Count);
    }

    [TestMethod]
    public void UnknownFormattingFieldsCannotDisappearDuringAMerge()
    {
        var baseline = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        baseline.Metadata.Formatting = Formatting();
        var xml = baseline.Clone(); xml.Metadata.Formatting.ReferencePrefix = "Sheet ";
        var native = baseline.Clone();
        native.Metadata.Formatting = SchematicFormattingSettings.Parser.ParseFrom(
            native.Metadata.Formatting.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray());
        var preserved = native.Clone();
        var error = Assert.ThrowsExactly<AutomationException>(() => SchematicItemMerge.Plan(baseline, xml, native));
        Assert.AreEqual("invalid_schematic_xml", error.Code);
        Assert.AreEqual(preserved, native);
    }
}
