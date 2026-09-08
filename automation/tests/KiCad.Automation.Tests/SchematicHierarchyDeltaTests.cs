using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicHierarchyDeltaTests
{
    [TestMethod]
    public void VariantRegistryHasOneProjectOwnerAcrossRepeatedSheets()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.VariantDescriptions.Add("Assembly", "Near heatsink");
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        Assert.AreEqual("Near heatsink", operations.Single().ReplaceVariantRegistry.Descriptions["Assembly"]);
        after.Instances[^1].Metadata.VariantDescriptions["Assembly"] = "Contradiction";
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void TextVariablesAreProjectWideAndEmittedOnlyOnce()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances) screen.Metadata.TextVariables.Add("NOTE", "Place near connector");
        var operations = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, operations.Count);
        Assert.AreEqual("Place near connector", operations.Single().ReplaceTextVariables.Variables["NOTE"]);
        after.Instances[^1].Metadata.TextVariables["NOTE"] = "Contradiction";
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void ProjectBusAliasesAreEmittedOnceAndConflictingSheetCopiesReject()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        foreach (var screen in after.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual("DATA", plan.Single().ReplaceBusAliases.Aliases.Single().Name);
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(after, after).Count);
        after.Instances[^1].Metadata.BusAliases[0].Members.Add("D2");
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
    }

    [TestMethod]
    public void AnotherInstanceUsesExistingContentsAndRejectsConflictingCopies()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        var repeated = after.Instances[0].Items[0].Unpack<SheetSymbol>();
        repeated.Id.Value = Guid.NewGuid().ToString("D");
        after.Instances[0].Items.Add(Any.Pack(repeated));
        var instance = after.Instances[1].Clone();
        instance.Metadata.Document.SheetPath.Path[^1] = repeated.Id.Clone();
        after.Instances.Add(instance);
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual(repeated, plan[0].Create.Unpack<SheetSymbol>());
        var bad = after.Clone(); var note = bad.Instances[^1].Items[0].Unpack<SchematicText>();
        note.Text.Text_ = "Conflicting shared contents"; bad.Instances[^1].Items[0] = Any.Pack(note);
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad));
        bad = after.Clone(); bad.Instances[^1].Metadata.TitleBlock = new() { Title = "Conflicting metadata" };
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad));
    }

    [TestMethod]
    public void NewNestedScreensAreCreatedBeforeTheirContentsWithoutDiscardingUnknownState()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        var template = before.Instances[0].Items[0].Unpack<SheetSymbol>();
        SchematicScreenData Add(SchematicScreenData parent)
        {
            var sheet = template.Clone(); sheet.Id.Value = Guid.NewGuid().ToString("D");
            sheet.ChildScreenId.Value = Guid.NewGuid().ToString("D"); sheet.Path = parent.Metadata.Document.SheetPath.Clone();
            sheet.FilenameField.Text.Text_ = sheet.Id.Value + ".kicad_sch";
            parent.Items.Add(Any.Pack(sheet));
            var screen = new SchematicScreenData { Metadata = new() { ScreenId = sheet.ChildScreenId.Clone(),
                Document = parent.Metadata.Document.Clone(), TitleBlock = new() { Title = "Generated sheet" } } };
            screen.Metadata.Document.SheetPath.Path.Add(sheet.Id.Clone());
            screen.Items.Add(Any.Pack(new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "New contents" } }));
            after.Instances.Add(screen); return screen;
        }
        var child = Add(after.Instances[0]); var nested = Add(child);
        var plan = SchematicHierarchyDelta.Plan(before, after).ToList();
        var creates = plan.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true).ToArray();
        Assert.AreEqual(2, creates.Length);
        foreach (var operation in creates)
        {
            var sheet = operation.Create.Unpack<SheetSymbol>();
            var target = operation.TargetDocument.Clone(); target.SheetPath.Path.Add(sheet.Id.Clone());
            Assert.IsTrue(plan.FindIndex(o => o.TargetDocument.Equals(target)) > plan.IndexOf(operation));
        }
        Assert.AreEqual(2, plan.Count(o => o.SetTitleBlock is not null));
        var removal = SchematicHierarchyDelta.Plan(after, before);
        Assert.AreEqual(1, removal.Count, "A removed subtree needs one parent-reference removal, not deletion of its contents.");
        Assert.AreEqual(child.Metadata.Document.SheetPath.Path[^1], removal[0].Remove);
        Assert.AreEqual(before.Document, removal[0].TargetDocument);
        var reparented = after.Clone();
        var oldParent = reparented.Instances.Single(s => s.Metadata.Document.Equals(child.Metadata.Document));
        var moved = oldParent.Items.Single(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
        oldParent.Items.Remove(Any.Pack(moved)); moved.Path = before.Document.SheetPath.Clone();
        reparented.Instances[0].Items.Add(Any.Pack(moved));
        var movedScreen = reparented.Instances.Single(s => s.Metadata.ScreenId.Equals(nested.Metadata.ScreenId));
        movedScreen.Metadata.Document = before.Document.Clone(); movedScreen.Metadata.Document.SheetPath.Path.Add(moved.Id.Clone());
        var movePlan = SchematicHierarchyDelta.Plan(after, reparented);
        Assert.AreEqual(2, movePlan.Count);
        Assert.AreEqual(moved.Id, movePlan.Single(o => o.Remove is not null).Remove);
        Assert.AreEqual(moved, movePlan.Single(o => o.Create is not null).Create.Unpack<SheetSymbol>());
        foreach (string unsupported in new[] { "unknown", "alias", "provenance", "root" })
        {
            var bad = after.Clone(); var metadata = bad.Instances[^1].Metadata;
            switch (unsupported)
            {
                case "unknown": metadata.UnrepresentedState.Add("library_cache"); break;
                case "alias": metadata.BusAliases.Add(new SchematicBusAlias { Name = "uninitialized" }); break;
                case "provenance": metadata.LoadedNativeFormatVersion = 20250114; break;
                case "root": metadata.RootInstance = new() { PageNumber = "7" }; break;
            }
            Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad), unsupported);
        }
        foreach (var screen in after.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        var withAliases = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, withAliases.Count(o => o.ReplaceBusAliases is not null));
        Assert.AreEqual(2, withAliases.Count(o => o.Create?.Is(SheetSymbol.Descriptor) == true));
        foreach (var screen in before.Instances)
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(before, after).Count(o => o.ReplaceBusAliases is not null));
        foreach (var screen in after.Instances) screen.Metadata.TextVariables.Add("NOTE", "New hierarchy");
        Assert.AreEqual(1, SchematicHierarchyDelta.Plan(before, after).Count(o => o.ReplaceTextVariables is not null));
    }

    [TestMethod]
    public void SharedSymbolMovesRetainDifferentReferencesAndUnitsInCompleteRecords()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        var records = new SymbolSheetRecords();
        for (int i = 1; i < before.Instances.Count; ++i)
        {
            var record = new SymbolSheetRecord { Reference = "U" + i, Unit = i, ProjectName = "fixture", Variants = new() };
            record.Variants.Variants.Add(new SchematicSymbolVariant { Name = "assembly", Attributes = new() { DoNotPopulate = i == 2 } });
            record.Path.Add(before.Instances[i].Metadata.Document.SheetPath.Path);
            records.Records.Add(record);
        }
        string identity = Guid.NewGuid().ToString("D");
        for (int i = 1; i < before.Instances.Count; ++i)
        {
            before.Instances[i].Items.Add(Any.Pack(new SchematicSymbolInstance
            {
                Id = new() { Value = identity }, Path = before.Instances[i].Metadata.Document.SheetPath.Clone(),
                Position = new(), ReferenceField = new() { Text = new() { Text_ = "U" + i,
                    Attributes = new() { Multiline = true } } },
                Unit = new() { Unit = i }, InstanceRecords = records.Clone(), Variants = records.Records[i - 1].Variants.Clone()
            }));
        }
        var after = before.Clone();
        foreach (var screen in after.Instances.Skip(1))
        {
            var symbol = screen.Items[^1].Unpack<SchematicSymbolInstance>(); symbol.Position.XNm = 1000000;
            screen.Items[^1] = Any.Pack(symbol);
        }
        string desired = SchematicDataXml.Write(after);
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(2, plan.Count);
        Assert.AreEqual(before.Instances[1].Metadata.ScreenId, plan[1].ReplaceLibraryCache.ScreenId);
        Assert.AreEqual(0, plan[1].ReplaceLibraryCache.Definitions.Count);
        Assert.AreEqual(records, plan[0].Update.Unpack<SchematicSymbolInstance>().InstanceRecords);
        Assert.AreEqual(desired, SchematicDataXml.Write(after));
        foreach (string invalid in new[] { "reference", "unit", "path", "records", "geometry", "variant", "description" })
        {
            var bad = after.Clone(); var symbol = bad.Instances[2].Items[^1].Unpack<SchematicSymbolInstance>();
            switch (invalid)
            {
                case "reference": symbol.ReferenceField.Text.Text_ = "U99"; break;
                case "unit": symbol.Unit.Unit = 9; break;
                case "path": symbol.Path = before.Document.SheetPath.Clone(); break;
                case "records": symbol.InstanceRecords = null; break;
                case "geometry": symbol.Position.XNm++; break;
                case "variant": symbol.Variants.Variants[0].Attributes.DoNotPopulate = false; break;
                case "description": symbol.Variants.Variants[0].Description = "Changed project description"; break;
            }
            bad.Instances[2].Items[^1] = Any.Pack(symbol);
            Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, bad), invalid);
        }
    }

    [TestMethod]
    public void SharedScreenEditsAreTargetedOnceAndOrderIndependent()
    {
        var before = SchematicHierarchyTopologyTests.Fixture();
        var after = before.Clone();
        foreach (var screen in after.Instances.Skip(1))
        {
            var note = screen.Items[0].Unpack<SchematicText>();
            note.Text.Text_ = "Updated shared note"; screen.Items[0] = Any.Pack(note);
        }
        string original = SchematicDataXml.Write(before), desired = SchematicDataXml.Write(after);
        var plan = SchematicHierarchyDelta.Plan(before, after);
        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual("Updated shared note", plan[0].Update.Unpack<SchematicText>().Text.Text_);
        Assert.AreEqual(2, plan[0].TargetDocument.SheetPath.Path.Count);
        var reversed = after.Clone(); reversed.Instances.Clear(); reversed.Instances.Add(after.Instances.Reverse());
        CollectionAssert.AreEqual(plan.ToArray(), SchematicHierarchyDelta.Plan(before, reversed).ToArray());
        Assert.AreEqual(original, SchematicDataXml.Write(before)); Assert.AreEqual(desired, SchematicDataXml.Write(after));
        Assert.AreEqual(0, SchematicHierarchyDelta.Plan(before, before).Count);
    }

    [TestMethod]
    public void ConflictingSharedEditsAndMissingInstancesRejectWithoutPartialPlan()
    {
        var before = SchematicHierarchyTopologyTests.Fixture(); var after = before.Clone();
        var note = after.Instances[1].Items[0].Unpack<SchematicText>();
        note.Text.Text_ = "Only one instance changed"; after.Instances[1].Items[0] = Any.Pack(note);
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        after = before.Clone(); after.Instances.RemoveAt(1);
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        after = before.Clone(); after.Instances[1].Metadata.EmbeddedFonts = true;
        Assert.ThrowsExactly<AutomationException>(() => SchematicHierarchyDelta.Plan(before, after));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicHierarchyDelta.Plan(before, before, cancelled.Token));
    }
}
