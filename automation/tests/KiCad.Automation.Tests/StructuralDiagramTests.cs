using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class StructuralDiagramTests
{
    [TestMethod]
    public void AbstractLinkCanCarryConcreteRealizationWithoutBecomingARequirement()
    {
        var (circuit, diagram) = Fixture();
        diagram.Validate(circuit);
        Assert.AreEqual(1, diagram.Connections[0].NetIds.Count);
        Assert.IsNull(diagram.Statements.Single(s => s.Role == EngineeringStatementRole.Realization).Strength);
        Assert.IsNotNull(diagram.Statements.Single(s => s.Role == EngineeringStatementRole.Realization).Connection);
        Assert.AreEqual(GuidanceStrength.Requirement, diagram.Statements.Single(s => s.Role == EngineeringStatementRole.Intent).Strength);
        Assert.IsNull(diagram.Statements.Single(s => s.Role == EngineeringStatementRole.Intent).Connection);
    }

    [TestMethod]
    public void SpecificityDoesNotChooseStrengthAndRealizationCannotAcquireStrength()
    {
        var (circuit, diagram) = Fixture();
        EngineeringStatement realization = diagram.Statements[1];
        (diagram with { Statements = [realization with { Role = EngineeringStatementRole.Intent,
            Strength = GuidanceStrength.Preference, DerivedFrom = [] }] }).Validate(circuit);
        Reject(diagram with { Statements = [realization with { Strength = GuidanceStrength.Requirement }] }, circuit);
        Reject(diagram with { Statements = [diagram.Statements[0] with { Strength = null }] }, circuit);
    }

    [TestMethod]
    public void AbstractConnectionDoesNotRequireAResolvedNetOrComponent()
    {
        var (circuit, diagram) = Fixture();
        (diagram with
        {
            Blocks = diagram.Blocks.Select(b => b with { ComponentIds = [] }).ToArray(),
            Connections = diagram.Connections.Select(c => c with { NetIds = [] }).ToArray(),
            Statements = [diagram.Statements[0]]
        }).Validate(circuit);
    }

    [TestMethod]
    public void InvalidObjectMappingsAndCyclesFailWithoutGuessing()
    {
        var (circuit, diagram) = Fixture();
        Reject(diagram with { Connections = [diagram.Connections[0] with { NetIds = [Guid.NewGuid()] }] }, circuit);
        Reject(diagram with { Ports = [diagram.Ports[0] with { BlockId = Guid.NewGuid() }, diagram.Ports[1]] }, circuit);
        Reject(diagram with { Blocks = [diagram.Blocks[0] with { ParentId = diagram.Blocks[1].Id },
            diagram.Blocks[1] with { ParentId = diagram.Blocks[0].Id }] }, circuit);
        Reject(diagram with { Statements = [diagram.Statements[0] with { DerivedFrom = [diagram.Statements[1].Id] }, diagram.Statements[1]] }, circuit);
        Reject(diagram with { Statements = [diagram.Statements[1]] }, circuit);
    }

    [TestMethod]
    public void DanglingConcretePinsDoNotPassAsValidRealization()
    {
        var (circuit, diagram) = Fixture();
        var invalid = diagram.Statements[1] with { Connection = new(new(circuit.Components[0].Id, "absent"), new(circuit.Components[1].Id, "8")) };
        Reject(diagram with { Statements = [diagram.Statements[0], invalid] }, circuit);
    }

    private static void Reject(StructuralDiagram diagram, Circuit circuit) =>
        Assert.ThrowsExactly<AutomationException>(() => diagram.Validate(circuit));

    internal static (Circuit, StructuralDiagram) Fixture()
    {
        Circuit circuit = CircuitXmlTests.Fixture();
        Guid diagram = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), pa = Guid.NewGuid(), pb = Guid.NewGuid();
        Guid link = Guid.NewGuid(), intent = Guid.NewGuid();
        return (circuit, new(diagram,
            [new(a, "Processing", null, [circuit.Components[0].Id]), new(b, "Peripheral", null, [circuit.Components[1].Id])],
            [new(pa, a, "Power"), new(pb, b, "Power")],
            [new(link, pa, pb, StructuralConnectionKind.Power, "Shared supply", [circuit.Nets[0].Id])],
            [new(intent, link, EngineeringStatementRole.Intent, GuidanceStrength.Requirement,
                 "Both blocks must share the specified supply domain.", null, [], []),
             new(Guid.NewGuid(), link, EngineeringStatementRole.Realization, null, "Implemented assignment",
                 new(new(circuit.Components[0].Id, "8"), new(circuit.Components[1].Id, "8")), [intent], [])]));
    }
}
