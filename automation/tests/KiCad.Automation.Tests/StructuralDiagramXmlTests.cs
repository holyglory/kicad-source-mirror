using System.Xml.Linq;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class StructuralDiagramXmlTests
{
    [TestMethod]
    public void EveryArchitecturalFieldAndExactUserTextSurviveReload()
    {
        var (circuit, original) = StructuralDiagramTests.Fixture();
        var source = new SourceReference("datasheet\tA", "revision\r\nB", 17, "Table 3", "industrial");
        var diagram = original with
        {
            Blocks = [original.Blocks[0], original.Blocks[1] with { ParentId = original.Blocks[0].Id }],
            Statements = [original.Statements[0] with { Text = "  Keep near the edge.\r\n\tAvoid the connector.\rFinal\n", Sources = [source] },
                original.Statements[1]]
        };
        string xml = StructuralDiagramXml.Write(diagram, circuit);
        var read = StructuralDiagramXml.Read(xml, circuit);
        Assert.AreEqual(xml, StructuralDiagramXml.Write(read, circuit));
        Assert.AreEqual(diagram.Id, read.Id);
        foreach (var block in diagram.Blocks)
        {
            var actual = read.Blocks.Single(b => b.Id == block.Id);
            Assert.AreEqual(block.Name, actual.Name); Assert.AreEqual(block.ParentId, actual.ParentId);
            CollectionAssert.AreEquivalent(block.ComponentIds.ToArray(), actual.ComponentIds.ToArray());
        }
        CollectionAssert.AreEquivalent(diagram.Ports.ToArray(), read.Ports.ToArray());
        var connection = read.Connections.Single(); var expected = diagram.Connections.Single();
        Assert.AreEqual(expected.Id, connection.Id); Assert.AreEqual(expected.Kind, connection.Kind);
        Assert.AreEqual(expected.FirstPortId, connection.FirstPortId); Assert.AreEqual(expected.SecondPortId, connection.SecondPortId);
        Assert.AreEqual(expected.Description, connection.Description);
        CollectionAssert.AreEquivalent(expected.NetIds.ToArray(), connection.NetIds.ToArray());
        foreach (var statement in diagram.Statements)
        {
            var actual = read.Statements.Single(s => s.Id == statement.Id);
            Assert.AreEqual(statement.TargetId, actual.TargetId); Assert.AreEqual(statement.Role, actual.Role);
            Assert.AreEqual(statement.Strength, actual.Strength); Assert.AreEqual(statement.Text, actual.Text);
            Assert.AreEqual(statement.Connection, actual.Connection);
            CollectionAssert.AreEquivalent(statement.DerivedFrom.ToArray(), actual.DerivedFrom.ToArray());
            CollectionAssert.AreEqual(statement.Sources.ToArray(), actual.Sources.ToArray());
        }
    }

    [TestMethod]
    public void EntityOrderingDoesNotCreateXmlChurn()
    {
        var (circuit, diagram) = StructuralDiagramTests.Fixture();
        string xml = StructuralDiagramXml.Write(diagram, circuit);
        Assert.AreEqual(xml, StructuralDiagramXml.Write(diagram with
        {
            Blocks = diagram.Blocks.Reverse().ToArray(), Ports = diagram.Ports.Reverse().ToArray(),
            Statements = diagram.Statements.Reverse().ToArray(), Connections = diagram.Connections.Reverse().ToArray()
        }, circuit));
    }

    [TestMethod]
    public void EveryStrengthKindAndInterpretationRoundTripWithoutInferringConnections()
    {
        var (circuit, original) = StructuralDiagramTests.Fixture();
        foreach (var strength in Enum.GetValues<GuidanceStrength>())
        foreach (var kind in Enum.GetValues<StructuralConnectionKind>())
        {
            var diagram = original with
            {
                Connections = [original.Connections[0] with { Kind = kind, NetIds = [] }],
                Statements = [original.Statements[0] with { Role = EngineeringStatementRole.Interpretation, Strength = strength }]
            };
            var read = StructuralDiagramXml.Read(StructuralDiagramXml.Write(diagram, circuit), circuit);
            Assert.AreEqual(strength, read.Statements[0].Strength);
            Assert.AreEqual(EngineeringStatementRole.Interpretation, read.Statements[0].Role);
            Assert.AreEqual(kind, read.Connections[0].Kind);
            Assert.IsEmpty(read.Connections[0].NetIds);
            Assert.IsNull(read.Statements[0].Connection);
        }
    }

    [TestMethod]
    public void UnknownAndIncompleteXmlIsRejectedRatherThanSimplified()
    {
        var (circuit, diagram) = StructuralDiagramTests.Fixture();
        string xml = StructuralDiagramXml.Write(diagram, circuit);
        XNamespace ns = StructuralDiagramXml.Namespace;
        foreach (Action<XElement> corrupt in new Action<XElement>[]
        {
            root => root.SetAttributeValue("version", 2),
            root => root.SetAttributeValue("future", "value"),
            root => root.Add(new XElement(ns + "future")),
            root => root.Element(ns + "ports")!.Remove(),
            root => root.Descendants(ns + "statement").First().SetAttributeValue("role", "Unknown"),
            root => root.Descendants(ns + "statement").First().Add(new XElement(ns + "future")),
            root => root.Descendants(ns + "first").First().SetAttributeValue("component", "invalid")
        })
        {
            XElement root = XElement.Parse(xml); corrupt(root);
            Assert.AreEqual("invalid_structural_xml", Assert.ThrowsExactly<AutomationException>(
                () => StructuralDiagramXml.Read(root.ToString(), circuit)).Code);
        }
        Assert.ThrowsExactly<AutomationException>(() => StructuralDiagramXml.Read(
            "<!DOCTYPE structure SYSTEM 'file:///not-read'>" + xml, circuit));
    }

    [TestMethod]
    public void AmbiguousIdentitiesAndInvalidSourceCoordinatesCannotBePersisted()
    {
        var (circuit, diagram) = StructuralDiagramTests.Fixture();
        Assert.ThrowsExactly<AutomationException>(() => StructuralDiagramXml.Write(diagram with { Id = circuit.Components[0].Id }, circuit));
        foreach (var source in new[] { new SourceReference("", "r1", 1, null, null),
            new SourceReference("data", " ", 1, null, null), new SourceReference("data", "r1", 0, null, null) })
            Assert.ThrowsExactly<AutomationException>(() => StructuralDiagramXml.Write(diagram with
                { Statements = [diagram.Statements[0] with { Sources = [source] }] }, circuit));
        string xml = StructuralDiagramXml.Write(diagram, circuit);
        Assert.ThrowsExactly<AutomationException>(() => StructuralDiagramXml.Read(
            xml.Replace("number=\"8\"", "number=\"missing\"", StringComparison.Ordinal), circuit));
    }
}
