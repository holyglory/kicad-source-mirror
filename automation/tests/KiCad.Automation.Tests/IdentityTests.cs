using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class IdentityTests
{
    [TestMethod]
    public void DifferentEpochIsStaleEvenWhenSequenceMatches()
    {
        var actual = new DocumentRevision("new-process", 4);
        AutomationException error = Assert.ThrowsExactly<AutomationException>(() => actual.RequireSame(new("old-process", 4)));
        Assert.AreEqual("stale_revision", error.Code);
        actual.RequireSame(new("new-process", 4));
    }

    [TestMethod]
    public void ReusedSheetObjectsHaveDifferentInstanceIdentity()
    {
        Guid objectId = Guid.NewGuid();
        var first = new SchematicObjectId(objectId, [Guid.NewGuid()]);
        var second = new SchematicObjectId(objectId, [Guid.NewGuid()]);
        Assert.AreNotEqual(first.Key, second.Key);
        Assert.AreEqual(first.Key, new SchematicObjectId(objectId, first.SheetPath.ToArray()).Key);
    }

    [TestMethod]
    public void UnitConversionDoesNotSilentlyRound()
    {
        Assert.AreEqual(12_700_000L, Coordinates.MillimetersToNanometers(12.7m));
        Assert.AreEqual(-127_000L, Coordinates.MillimetersToSchematicUnits(-12.7m));
        Assert.AreEqual(12.700001m, Coordinates.NanometersToMillimeters(12_700_001));
        Assert.ThrowsExactly<AutomationException>(() => Coordinates.MillimetersToSchematicUnits(0.000001m));
        Assert.ThrowsExactly<AutomationException>(() => Coordinates.MillimetersToNanometers(0.0000001m));
    }
}
