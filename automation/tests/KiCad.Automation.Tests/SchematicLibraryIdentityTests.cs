using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicLibraryIdentityTests
{
    [TestMethod]
    public void XmlKeepsThePlacementLibraryLinkDefinitionAndCacheAliasDistinct()
    {
        var screen = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        var symbol = new SchematicSymbolInstance
        {
            Id = new() { Value = Guid.NewGuid().ToString("D") },
            Path = screen.Metadata.Document.SheetPath.Clone(),
            LibraryId = new() { LibraryNickname = "Catalog", EntryName = "Original" },
            Definition = new() { Id = new() { LibraryNickname = "Local", EntryName = "Edited" } },
            LibName = "ScreenCopy_3", SeparatePinIdentities = true,
            PinNameOffset = new() { ValueNm = 123400 }, DefinitionPinNameOffset = new() { ValueNm = 0 }
        };
        screen.Items.Add(Any.Pack(symbol));
        var encoded = SchematicDataXml.Write(screen);
        var decoded = (SchematicScreenData)SchematicDataXml.Read(encoded);
        Assert.AreEqual(screen, decoded);
        var recovered = decoded.Items.Last().Unpack<SchematicSymbolInstance>();
        Assert.AreEqual("Catalog", recovered.LibraryId.LibraryNickname);
        Assert.AreEqual("Original", recovered.LibraryId.EntryName);
        Assert.AreEqual("Local", recovered.Definition.Id.LibraryNickname);
        Assert.AreEqual("Edited", recovered.Definition.Id.EntryName);
        Assert.AreEqual("ScreenCopy_3", recovered.LibName);
        Assert.AreEqual(123400L, recovered.PinNameOffset.ValueNm);
        Assert.IsNotNull(recovered.DefinitionPinNameOffset);
        Assert.AreEqual(0L, recovered.DefinitionPinNameOffset.ValueNm);
        var legacy = symbol.Clone(); legacy.LibraryId = null;
        screen.Items[^1] = Any.Pack(legacy);
        var legacyDecoded = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(screen));
        Assert.IsNull(legacyDecoded.Items.Last().Unpack<SchematicSymbolInstance>().LibraryId);
    }

    [TestMethod]
    public void XmlDistinguishesDeMorganSemanticsFromMatchingCustomStyleNames()
    {
        var screen = SchematicHierarchyTopologyTests.Fixture().Instances[0].Clone();
        var definition = new SchematicSymbol();
        definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Standard" });
        definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Alternate" });
        foreach (bool demorgan in new[] { false, true })
        {
            definition.DemorganBodyStyles = demorgan;
            screen.CachedSymbols.Clear();
            screen.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = "Test:BodyStyles", Definition = definition.Clone() });
            var decoded = (SchematicScreenData)SchematicDataXml.Read(SchematicDataXml.Write(screen));
            Assert.AreEqual(demorgan, decoded.CachedSymbols.Single().Definition.DemorganBodyStyles);
            Assert.AreEqual(screen, decoded);
            Assert.AreEqual(0, SchematicItemDelta.Plan(decoded, decoded.Clone()).Count);
            if (demorgan)
            {
                var invalid = decoded.Clone();
                invalid.CachedSymbols.Single().Definition.BodyStyle[1].Name = "Custom";
                Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemDelta.Plan(decoded, invalid));
                invalid.CachedSymbols.Single().Definition.BodyStyle.RemoveAt(1);
                Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => SchematicItemDelta.Plan(decoded, invalid));
            }
        }
    }
}
