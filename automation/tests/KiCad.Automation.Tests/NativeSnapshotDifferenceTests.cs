using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeSnapshotDifferenceTests
{
    [TestMethod]
    public void ReportsExactChangedFieldAndKeepsDiagnosticsBounded()
    {
        var before = new SchematicScreenData();
        before.Items.Add(Google.Protobuf.WellKnownTypes.Any.Pack(new SheetSymbol()));
        for (int i = 0; i < 20; ++i)
            before.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Library:" + i });
        var after = before.Clone();
        foreach (var entry in after.CachedSymbols) entry.CacheKey += "-changed";
        string report = NativeSnapshotDifference.Describe(before, after);
        StringAssert.Contains(report, "$.cachedSymbols[0].cacheKey");
        Assert.AreEqual(8, report.Split(Environment.NewLine).Length);
        Assert.AreEqual("", NativeSnapshotDifference.Describe(before, before.Clone()));
    }
}
