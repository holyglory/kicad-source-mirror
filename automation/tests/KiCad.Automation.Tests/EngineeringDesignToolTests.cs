using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class EngineeringDesignToolTests
{
    [TestMethod]
    public void ValidationReportsUnrealizedConnectionsAndRetainsGuidanceConflicts()
    {
        var (design, library) = EngineeringDesignXmlTests.Fixture();
        design = design with
        {
            Structure = design.Structure with
                { Connections = design.Structure.Connections.Select(c => c with { NetIds = [] }).ToArray() },
            ComponentBindings = [design.ComponentBindings[0] with
                { Guidance = [design.ComponentBindings[0].Guidance[0] with { Key = "usage" }] }]
        };
        string xml = EngineeringDesignXml.Write(design, [library]);
        var result = new KnowledgeTools().ValidateDesign(xml, [ComponentKnowledgeXml.WriteLibrary(library)], CancellationToken.None);
        Assert.IsTrue(result.ModelValid);
        Assert.AreEqual(design.Circuit.Id, result.DesignId);
        Assert.AreEqual(1, result.Guidance![design.ComponentBindings[0].ComponentInstanceId].Conflicts.Count);
        CollectionAssert.AreEqual(design.Structure.Connections.Select(c => c.Id).Order().ToArray(), result.UnrealizedConnections!.ToArray());
    }

    [TestMethod]
    public void FailureAndRecoveryAreExplicitAndCancellationIsNotSuccess()
    {
        var (design, library) = EngineeringDesignXmlTests.Fixture();
        string xml = EngineeringDesignXml.Write(design, [library]);
        var tools = new KnowledgeTools();
        var failed = tools.ValidateDesign(xml, [], CancellationToken.None);
        Assert.IsFalse(failed.ModelValid); Assert.IsNull(failed.Guidance); Assert.IsNull(failed.DesignId);
        Assert.AreEqual("missing_knowledge_library", failed.ErrorCode);
        Assert.IsFalse(tools.ValidateDesign("<broken>", [], CancellationToken.None).ModelValid);
        Assert.ThrowsExactly<OperationCanceledException>(() => tools.ValidateDesign(xml,
            [ComponentKnowledgeXml.WriteLibrary(library)], new CancellationToken(canceled: true)));
        Assert.IsTrue(tools.ValidateDesign(xml, [ComponentKnowledgeXml.WriteLibrary(library)], CancellationToken.None).ModelValid);
    }
}
