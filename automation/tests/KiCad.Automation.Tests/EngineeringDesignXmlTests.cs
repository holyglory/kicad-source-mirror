using System.Xml.Linq;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class EngineeringDesignXmlTests
{
    [TestMethod]
    public void CombinedDesignRetainsArchitectureCircuitAndLocalVersusInheritedGuidance()
    {
        var (design, library) = Fixture();
        var local = design.ComponentBindings[0].Guidance[0] with
        {
            Text = "  Prefer side A.\r\n\tKeep the thermal path clear.\rFinal\n",
            Sources = [new("requirements\tA", "rev\r\n2", 3, "thermal", "assembly-A")]
        };
        design = design with { ComponentBindings = [design.ComponentBindings[0] with { Guidance = [local] }] };
        string libraryBefore = ComponentKnowledgeXml.WriteLibrary(library);
        string xml = EngineeringDesignXml.Write(design, [library]);
        var read = EngineeringDesignXml.Read(xml, [library]);
        Assert.AreEqual(xml, EngineeringDesignXml.Write(read, [library]));
        Assert.AreEqual(CircuitXml.Write(design.Circuit), CircuitXml.Write(read.Circuit));
        Assert.AreEqual(StructuralDiagramXml.Write(design.Structure, design.Circuit),
            StructuralDiagramXml.Write(read.Structure, read.Circuit));
        CollectionAssert.AreEqual(design.KnowledgeLibraries.ToArray(), read.KnowledgeLibraries.ToArray());
        var actual = read.ComponentBindings.Single();
        Assert.AreEqual(design.ComponentBindings[0].ComponentInstanceId, actual.ComponentInstanceId);
        Assert.AreEqual(design.ComponentBindings[0].LibraryId, actual.LibraryId);
        Assert.AreEqual(design.ComponentBindings[0].LibraryRevision, actual.LibraryRevision);
        Assert.AreEqual(design.ComponentBindings[0].ClassId, actual.ClassId);
        Assert.AreEqual(local.Text, actual.Guidance.Single().Text);
        CollectionAssert.AreEqual(local.Sources.ToArray(), actual.Guidance.Single().Sources.ToArray());
        var resolved = read.Validate([library])[actual.ComponentInstanceId];
        Assert.AreEqual(1, resolved.Effective.Count(g => g.IsInstance));
        Assert.AreEqual(2, resolved.Effective.Count(g => !g.IsInstance));
        Assert.AreEqual(libraryBefore, ComponentKnowledgeXml.WriteLibrary(library));
        Assert.IsFalse(xml.Contains("Close to supply pins; exact distance is not specified.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingAndChangedLibrariesCannotSilentlyUpgradeOrDetachTheDesign()
    {
        var (design, library) = Fixture();
        string xml = EngineeringDesignXml.Write(design, [library]);
        Assert.AreEqual("missing_knowledge_library", Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Read(xml, [])).Code);
        Assert.AreEqual("library_revision_mismatch", Assert.ThrowsExactly<AutomationException>(() =>
            EngineeringDesignXml.Read(xml, [library with { Revision = "r2" }])).Code);
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Read(xml, [library, library]));
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Write(design with { KnowledgeLibraries = [] }, [library]));
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Write(design with
            { KnowledgeLibraries = [design.KnowledgeLibraries[0] with { Path = "../outside.xml" }] }, [library]));
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Write(design with
            { KnowledgeLibraries = [design.KnowledgeLibraries[0], design.KnowledgeLibraries[0]] }, [library]));
    }

    [TestMethod]
    public void ConflictingRequirementsRemainVisibleAndDoNotPreventLosslessStorage()
    {
        var (design, library) = Fixture();
        var duplicate = design.ComponentBindings[0].Guidance[0] with
            { Id = Guid.NewGuid(), Key = "usage", Text = "A conflicting local application instruction" };
        design = design with { ComponentBindings = [design.ComponentBindings[0] with { Guidance = [duplicate] }] };
        var read = EngineeringDesignXml.Read(EngineeringDesignXml.Write(design, [library]), [library]);
        Assert.AreEqual(1, read.Validate([library])[design.ComponentBindings[0].ComponentInstanceId].Conflicts.Count);
    }

    [TestMethod]
    public void WrongComponentAndDuplicateOwnershipAreRejectedWithoutGuessing()
    {
        var (design, library) = Fixture();
        var binding = design.ComponentBindings[0];
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Write(design with
            { ComponentBindings = [binding with { ComponentInstanceId = Guid.NewGuid() }] }, [library]));
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Write(design with
            { ComponentBindings = [binding, binding] }, [library]));
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Write(design with
            { ComponentBindings = [binding, binding with { ComponentInstanceId = design.Circuit.Components[1].Id }] }, [library]));
    }

    [TestMethod]
    public void UnknownFieldsAndVersionsInAnySectionAreRejected()
    {
        var (design, library) = Fixture();
        string xml = EngineeringDesignXml.Write(design, [library]);
        XNamespace ns = EngineeringDesignXml.Namespace;
        foreach (Action<XElement> corrupt in new Action<XElement>[]
        {
            root => root.SetAttributeValue("version", 2),
            root => root.SetAttributeValue("future", "value"),
            root => root.Add(new XElement(ns + "future")),
            root => root.Element(ns + "knowledge-libraries")!.Remove(),
            root => root.Element(XName.Get("circuit", CircuitXml.Namespace))!.SetAttributeValue("future", "value"),
            root => root.Element(XName.Get("structure", StructuralDiagramXml.Namespace))!.SetAttributeValue("version", 2),
            root => root.Descendants(XName.Get("component-binding", ComponentKnowledgeXml.Namespace)).Single().SetAttributeValue("future", "value")
        })
        {
            var root = XElement.Parse(xml); corrupt(root);
            Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Read(root.ToString(), [library]));
        }
        Assert.ThrowsExactly<AutomationException>(() => EngineeringDesignXml.Read(
            "<!DOCTYPE engineering-design SYSTEM 'file:///not-read'>" + xml, [library]));
    }

    [TestMethod]
    public void ModelWithoutGuidanceDependenciesStillRoundTrips()
    {
        var (design, _) = Fixture();
        design = design with { ComponentBindings = [], KnowledgeLibraries = [] };
        string xml = EngineeringDesignXml.Write(design, []);
        Assert.AreEqual(xml, EngineeringDesignXml.Write(EngineeringDesignXml.Read(xml, []), []));
    }

    internal static (EngineeringDesign, ComponentKnowledgeLibrary) Fixture()
    {
        var (circuit, structure) = StructuralDiagramTests.Fixture();
        var (library, binding) = ComponentGuidanceTests.Fixture();
        return (new(circuit, structure, [new(library.Id, "libraries/processors.xml", library.Revision)],
            [binding with { ComponentInstanceId = circuit.Components[0].Id }]), library);
    }
}
