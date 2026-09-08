using System.Xml.Linq;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicDesignTests
{
    [TestMethod]
    public void RepeatedSheetsAndMultipleUnitsResolveThroughExplicitIdentities()
    {
        var (design, library) = Fixture();
        var report = SchematicDesignBindings.Inspect(design, [library]);
        Assert.IsTrue(report.IdentitiesResolved, string.Join(",", report.Issues.Select(i => i.Code)));
        Assert.IsEmpty(report.Differences);
        Assert.AreEqual(2, report.CoverageGaps.Count, "Identity links must not hide incomplete native serialization.");
        Assert.AreEqual(4, design.SymbolBindings.Count);
        Assert.AreEqual(2, design.SymbolBindings.Select(b => b.NativeObjectId).Distinct().Count(),
            "Shared-screen object UUIDs repeat across distinct sheet paths.");
    }

    [TestMethod]
    public void OneDocumentPreservesEngineeringNativeDataAndUnresolvedBindings()
    {
        var (design, library) = Fixture();
        string xml = SchematicDesignXml.Write(design, [library]);
        var read = SchematicDesignXml.Read(xml, [library]);
        Assert.AreEqual(xml, SchematicDesignXml.Write(read, [library]));
        Assert.AreEqual(EngineeringDesignXml.Write(design.Engineering, [library]), EngineeringDesignXml.Write(read.Engineering, [library]));
        Assert.AreEqual(design.Schematic, read.Schematic);
        Assert.AreEqual(xml, SchematicDesignXml.Write(design with
        {
            SheetBindings = design.SheetBindings.Reverse().ToArray(), SymbolBindings = design.SymbolBindings.Reverse().ToArray()
        }, [library]));
        var pending = design with { SymbolBindings = [.. design.SymbolBindings, new(Guid.NewGuid(), Guid.NewGuid())] };
        string pendingXml = SchematicDesignXml.Write(pending, [library]);
        var restored = SchematicDesignXml.Read(pendingXml, [library]);
        Assert.AreEqual(pendingXml, SchematicDesignXml.Write(restored, [library]));
        Assert.IsTrue(SchematicDesignBindings.Inspect(restored, [library]).Issues.Any(i => i.Code == "unknown_model_symbol"));
    }

    [TestMethod]
    public void RenamingAndUnitChangesAreDriftNotIdentityHeuristics()
    {
        var (design, library) = Fixture();
        var screen = design.Schematic.Instances[1];
        int index = screen.Items.ToList().FindIndex(p => p.Is(SchematicSymbolInstance.Descriptor));
        var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>();
        symbol.ReferenceField.Text.Text_ = "U99"; symbol.Unit.Unit = 2;
        symbol.Position = new() { XNm = 123400, YNm = 567800 };
        screen.Items[index] = Any.Pack(symbol);
        var report = SchematicDesignBindings.Inspect(design, [library]);
        Assert.IsTrue(report.IdentitiesResolved);
        Assert.AreEqual(2, report.Differences.Count);
        Assert.IsTrue(report.Differences.Any(d => d.Field == "reference" && d.NativeValue == "U99"));
        Assert.IsTrue(report.Differences.Any(d => d.Field == "unit" && d.NativeValue == "2"));
        symbol.Id.Value = Guid.NewGuid().ToString("D"); screen.Items[index] = Any.Pack(symbol);
        report = SchematicDesignBindings.Inspect(design, [library]);
        Assert.IsFalse(report.IdentitiesResolved);
        Assert.IsTrue(report.Issues.Any(i => i.Code == "missing_native_symbol"));
        Assert.IsTrue(report.Issues.Any(i => i.Code == "unmapped_native_symbol"));
    }

    [TestMethod]
    public void AmbiguousOwnershipAndWrongSheetPathsNeverSelectAnArbitraryObject()
    {
        foreach (string problem in new[] { "duplicate-binding", "duplicate-target", "duplicate-object", "wrong-symbol-path", "duplicate-sheet", "missing-sheet" })
        {
            var (design, library) = Fixture();
            switch (problem)
            {
                case "duplicate-binding": design = design with { SymbolBindings = [.. design.SymbolBindings, design.SymbolBindings[0]] }; break;
                case "duplicate-target": design = design with { SymbolBindings = design.SymbolBindings.Select((b, i) => i == 1
                    ? b with { NativeObjectId = design.SymbolBindings[0].NativeObjectId } : b).ToArray() }; break;
                case "duplicate-object": design.Schematic.Instances[1].Items.Add(design.Schematic.Instances[1].Items.First(p => p.Is(SchematicSymbolInstance.Descriptor)).Clone()); break;
                case "wrong-symbol-path":
                    var screen = design.Schematic.Instances[1];
                    int index = screen.Items.ToList().FindIndex(p => p.Is(SchematicSymbolInstance.Descriptor));
                    var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>(); symbol.Path = design.Schematic.Document.SheetPath.Clone();
                    screen.Items[index] = Any.Pack(symbol); break;
                case "duplicate-sheet": design = design with { SheetBindings = [.. design.SheetBindings, design.SheetBindings[1]] }; break;
                case "missing-sheet": design = design with { SheetBindings = design.SheetBindings.Skip(1).ToArray() }; break;
            }
            string before = SchematicDesignXml.Write(design, [library]);
            Assert.IsFalse(SchematicDesignBindings.Inspect(design, [library]).IdentitiesResolved, problem);
            Assert.AreEqual(before, SchematicDesignXml.Write(design, [library]), "Inspection must preserve every competing version.");
        }
    }

    [TestMethod]
    public void UnknownFieldsWrongNativeRootAndCancellationFailExplicitly()
    {
        var (design, library) = Fixture();
        string xml = SchematicDesignXml.Write(design, [library]);
        XNamespace ns = SchematicDesignXml.Namespace;
        foreach (Action<XElement> corrupt in new Action<XElement>[]
        {
            root => root.SetAttributeValue("version", 2), root => root.Add(new XElement(ns + "future")),
            root => root.Element(ns + "sheet-bindings")!.Elements().First().SetAttributeValue("future", "value"),
            root => root.Element(ns + "symbol-bindings")!.Remove(),
            root => root.Element(XName.Get("schematic-data", SchematicDataXml.Namespace))!.ReplaceWith(
                XElement.Parse(SchematicDataXml.Write(new SchematicText())))
        })
        {
            XElement root = XElement.Parse(xml); corrupt(root);
            Assert.ThrowsExactly<AutomationException>(() => SchematicDesignXml.Read(root.ToString(), [library]));
        }
        Assert.ThrowsExactly<AutomationException>(() => SchematicDesignXml.Read("<!DOCTYPE design SYSTEM 'file:///not-read'>" + xml, [library]));
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicDesignBindings.Inspect(design, [library], new CancellationToken(canceled: true)));
        var tools = new KnowledgeTools();
        Assert.ThrowsExactly<OperationCanceledException>(() => tools.InspectBindings(xml, [], new CancellationToken(canceled: true)));
        var failed = tools.InspectBindings(xml, [], CancellationToken.None);
        Assert.IsFalse(failed.DocumentParsed); Assert.IsNull(failed.BindingReport);
        Assert.AreEqual("missing_knowledge_library", failed.ErrorCode);
        Assert.IsTrue(tools.InspectBindings(xml, [ComponentKnowledgeXml.WriteLibrary(library)], CancellationToken.None).BindingReport!.IdentitiesResolved);
    }

    internal static (SchematicDesign, ComponentKnowledgeLibrary) Fixture()
    {
        var (engineering, library) = EngineeringDesignXmlTests.Fixture();
        var native = SchematicHierarchyTopologyTests.Fixture();
        Guid rootDefinition = Guid.NewGuid(), rootInstance = Guid.NewGuid();
        var circuit = engineering.Circuit with
        {
            Sheets = [new(rootDefinition, "Root", []), .. engineering.Circuit.Sheets],
            SheetInstances = [new(rootInstance, rootDefinition, null),
                .. engineering.Circuit.SheetInstances.Select(s => s with { ParentId = rootInstance })]
        };
        engineering = engineering with { Circuit = circuit };
        var sheets = new List<SchematicSheetBinding> { new(rootInstance,
            native.Document.SheetPath.Path.Select(id => Guid.Parse(id.Value)).ToArray()) };
        var symbols = new List<SchematicSymbolBinding>();
        var sharedIds = new Dictionary<int, Guid> { [1] = Guid.NewGuid(), [2] = Guid.NewGuid() };
        for (int i = 0; i < circuit.Components.Count; i++)
        {
            var component = circuit.Components[i]; var screen = native.Instances[i + 1];
            sheets.Add(new(component.SheetInstanceId, screen.Metadata.Document.SheetPath.Path.Select(id => Guid.Parse(id.Value)).ToArray()));
            string value = circuit.Sheets.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId).Value;
            foreach (var occurrence in circuit.Symbols.Where(s => s.ComponentId == component.Id))
            {
                Guid id = sharedIds[occurrence.Unit];
                var symbol = new SchematicSymbolInstance
                {
                    Id = new() { Value = id.ToString("D") }, Path = screen.Metadata.Document.SheetPath.Clone(),
                    Unit = new() { Unit = occurrence.Unit },
                    ReferenceField = new() { Text = new() { Text_ = component.Reference, Attributes = new() { Multiline = true } } },
                    ValueField = new() { Text = new() { Text_ = value, Attributes = new() { Multiline = true } } }
                };
                screen.Items.Add(Any.Pack(symbol)); symbols.Add(new(occurrence.Id, id));
            }
        }
        return (new(engineering, native, sheets, symbols), library);
    }
}
