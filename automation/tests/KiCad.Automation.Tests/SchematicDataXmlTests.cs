using System.Collections;
using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicDataXmlTests
{
    [TestMethod]
    public void VariantRegistryPreservesEmptyDescriptionsAndMultilingualInstructions()
    {
        var metadata = new SchematicMetadata();
        metadata.VariantDescriptions.Add("Assembly", "");
        metadata.VariantDescriptions.Add("低消費電力", "Place near heatsink & keep accessible.\nDo not populate R3.");
        var xml = SchematicDataXml.Write(metadata);
        Assert.AreEqual(metadata, SchematicDataXml.Read(xml));
        Assert.AreEqual(xml, SchematicDataXml.Write(SchematicDataXml.Read(xml)));
    }

    [TestMethod]
    public void NetChainDefinitionsPreserveTerminalsClassesColorsAndUnresolvedIntent()
    {
        var metadata = new SchematicMetadata();
        metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "DATA_PATH",
            From = new() { Reference = "U1", Pin = "D15" }, To = new() { Reference = "U2", Pin = "D11" },
            NetClass = "Matched", Color = new() { R = 0.2, G = 0.4, B = 0.6, A = 0.5 },
            MemberNets = { "/DATA_A", "/DATA_B" }, Committed = true });
        metadata.NetChains.Add(new SchematicNetChainDefinition { Name = "UNRESOLVED",
            From = new() { Reference = "MISSING", Pin = "1" }, To = new() { Reference = "U2", Pin = "2" },
            MemberNets = { "/UNKNOWN" } });
        var xml = SchematicDataXml.Write(metadata);
        Assert.AreEqual(metadata, SchematicDataXml.Read(xml));
    }
    [TestMethod]
    public void ScreenRecordPreservesTypedObjectsAndExplicitCoverageGaps()
    {
        var screen = new SchematicScreenData { Metadata = new SchematicMetadata() };
        screen.Metadata.UnrepresentedState.Add("library_cache");
        var text = new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Text = new() { Text_ = "User instruction: preserve this note" } };
        screen.Items.Add(Any.Pack(text));
        screen.UnrepresentedItems.Add(new SchematicUnrepresentedItem { Id = new() { Value = Guid.NewGuid().ToString("D") },
            NativeType = 999, Reason = "Synthetic future native object" });
        string xml = SchematicDataXml.Write(screen);
        StringAssert.Contains(xml, "kiapi.schematic.types.SchematicText");
        StringAssert.Contains(xml, "User instruction: preserve this note");
        var read = (SchematicScreenData)SchematicDataXml.Read(xml);
        Assert.AreEqual(screen, read);
        Assert.AreEqual(text, read.Items.Single().Unpack<SchematicText>());
        Assert.AreEqual("library_cache", read.Metadata.UnrepresentedState.Single());
        Assert.AreEqual(1, read.UnrepresentedItems.Count);
    }

    [TestMethod]
    public void EveryDeclaredSchematicDtoRoundTripsPopulatedFields()
    {
        foreach (var descriptor in SchematicText.Descriptor.File.MessageTypes)
        {
            var value = Populate(descriptor, 4);
            string xml = SchematicDataXml.Write(value);
            Assert.AreEqual(value, SchematicDataXml.Read(xml), descriptor.FullName);
            Assert.AreEqual(xml, SchematicDataXml.Write(SchematicDataXml.Read(xml)), descriptor.FullName);
        }
    }

    [TestMethod]
    public void UnplacedCacheDefinitionsRemainDistinctFromInstancesInXml()
    {
        var screen = new SchematicScreenData();
        screen.CachedSymbols.Add(new SchematicCachedSymbol
        {
            CacheKey = "Library:Unused_7", ShowPinNames = false, ShowPinNumbers = true,
            PinNameOffset = new Distance { ValueNm = 1270000 },
            Definition = new SchematicSymbol
            {
                Id = new LibraryIdentifier { LibraryNickname = "Library", EntryName = "Unused" },
                PinsUseLocalCoordinates = true, EmbeddedFiles = new EmbeddedFiles()
            }
        });
        var xml = SchematicDataXml.Write(screen);
        StringAssert.Contains(xml, "<cache_key>Library:Unused_7</cache_key>");
        var restored = (SchematicScreenData)SchematicDataXml.Read(xml);
        Assert.AreEqual(screen, restored);
        Assert.AreEqual(0, restored.Items.Count);
        Assert.AreEqual(1, restored.CachedSymbols.Count);
        Assert.AreEqual(xml, SchematicDataXml.Write(restored));
    }

    [TestMethod]
    public void NativeObjectsRemainStructuredAndPreserveWhitespaceAndExtremeCoordinates()
    {
        var text = new SchematicText
        {
            Id = new KIID { Value = Guid.NewGuid().ToString("D") },
            Text = new Text { Text_ = "  電源 & <pins>\r\n second\tline  ",
                Position = new Vector2 { XNm = long.MinValue, YNm = long.MaxValue } }
        };
        string xml = SchematicDataXml.Write(text);
        StringAssert.Contains(xml, "<x_nm>-9223372036854775808</x_nm>");
        StringAssert.Contains(xml, "<text>");
        Assert.AreEqual(text, SchematicDataXml.Read(xml));
        var child = new SchematicSymbolChild { Item = Any.Pack(text), Unit = new() };
        string childXml = SchematicDataXml.Write(child);
        StringAssert.Contains(childXml, "kiapi.schematic.types.SchematicText");
        Assert.AreEqual(child, SchematicDataXml.Read(childXml));
        Assert.IsNotNull(((SchematicSymbolChild)SchematicDataXml.Read(childXml)).Unit);
    }

    [TestMethod]
    public void MetadataXmlPreservesAssetBytesWithoutClaimingAssetValidation()
    {
        // Synthetic transport bytes test serialization only, not decompression,
        // hash validation or native asset restoration.
        var metadata = new SchematicMetadata { EmbeddedFiles = new EmbeddedFiles(), EmbeddedFonts = true };
        metadata.TextVariables.Add("NOTE", "電源 & timing");
        metadata.UnrepresentedState.Add("complete_project_settings");
        metadata.EmbeddedFiles.Files.Add(new EmbeddedFile
        {
            Name = "fixture.dat", Type = EmbeddedFileType.EftOther,
            Data = ByteString.CopyFrom(new byte[] { 0, 255, 10, 13, 128 }), DataHash = "synthetic-fixture-hash"
        });
        Assert.AreEqual(metadata, SchematicDataXml.Read(SchematicDataXml.Write(metadata)));
    }

    [TestMethod]
    public void SheetRecordsPreserveOptionalFalseFlagsAndReplacementPresence()
    {
        var sheet = new SheetSymbol { InstanceRecords = new SheetPlacementRecords() };
        var record = new SheetPlacementRecord
        {
            ProjectName = "Shared assembly", PageNumber = "3.B", Variants = new SheetVariants()
        };
        record.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
        record.Variants.Variants.Add(new SheetVariant
        {
            Name = "Optional", ExcludeFromSim = false, ExcludeFromBom = false, Dnp = false
        });
        sheet.InstanceRecords.Records.Add(record);
        string xml = SchematicDataXml.Write(sheet);
        StringAssert.Contains(xml, "<page_number>3.B</page_number>");
        var reconstructed = (SheetSymbol)SchematicDataXml.Read(xml);
        Assert.AreEqual(sheet, reconstructed);
        var variant = reconstructed.InstanceRecords.Records.Single().Variants.Variants.Single();
        Assert.IsTrue(variant.HasExcludeFromSim && variant.HasExcludeFromBom && variant.HasDnp);
        Assert.IsFalse(variant.ExcludeFromSim || variant.ExcludeFromBom || variant.Dnp);
        var absent = new SheetSymbol();
        var empty = new SheetSymbol { InstanceRecords = new SheetPlacementRecords() };
        Assert.IsNull(((SheetSymbol)SchematicDataXml.Read(SchematicDataXml.Write(absent))).InstanceRecords);
        Assert.IsNotNull(((SheetSymbol)SchematicDataXml.Read(SchematicDataXml.Write(empty))).InstanceRecords);
    }

    [TestMethod]
    public void SharedPlacementRecordsRemainStructuredAndDistinct()
    {
        var symbol = new SchematicSymbolInstance { InstanceRecords = new SymbolSheetRecords() };
        foreach (string reference in new[] { "U101", "U201" })
        {
            var record = new SymbolSheetRecord
            {
                Reference = reference, ProjectName = "Shared assembly", Unit = 2,
                Variants = new SchematicSymbolVariants()
            };
            record.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            symbol.InstanceRecords.Records.Add(record);
        }
        string xml = SchematicDataXml.Write(symbol);
        StringAssert.Contains(xml, "<instance_records>");
        StringAssert.Contains(xml, "<project_name>Shared assembly</project_name>");
        Assert.AreEqual(symbol, SchematicDataXml.Read(xml));
        var absent = new SchematicSymbolInstance();
        var empty = new SchematicSymbolInstance { InstanceRecords = new SymbolSheetRecords() };
        Assert.IsNull(((SchematicSymbolInstance)SchematicDataXml.Read(SchematicDataXml.Write(absent))).InstanceRecords);
        Assert.IsNotNull(((SchematicSymbolInstance)SchematicDataXml.Read(SchematicDataXml.Write(empty))).InstanceRecords);
    }

    [TestMethod]
    public void VariantXmlDistinguishesPreserveFromExplicitClear()
    {
        var preserve = new SchematicSymbolVariant { Name = "production" };
        var clear = preserve.Clone();
        clear.SymbolOverride = new LibraryIdentifier();
        clear.PinMapOverride = new PinMapInstanceOverride { Mode = PinMapOverrideMode.PmomUseLibraryDefault };
        var preserved = (SchematicSymbolVariant)SchematicDataXml.Read(SchematicDataXml.Write(preserve));
        var cleared = (SchematicSymbolVariant)SchematicDataXml.Read(SchematicDataXml.Write(clear));
        Assert.IsNull(preserved.SymbolOverride);
        Assert.IsNull(preserved.PinMapOverride);
        Assert.IsNotNull(cleared.SymbolOverride);
        Assert.AreEqual(clear, cleared);
        Assert.AreNotEqual(SchematicDataXml.Write(preserve), SchematicDataXml.Write(clear));
    }

    [TestMethod]
    public void UnknownFieldsVersionsTypesAndInvalidNativeEnumsAreRejected()
    {
        var text = new SchematicText();
        // Field 1000 (varint) is not declared by this native schema.
        text.MergeFrom(new byte[] { 0xc0, 0x3e, 0x01 });
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Write(text));
        string valid = SchematicDataXml.Write(new SchematicText());
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Read(valid.Replace("version=\"1\"", "version=\"2\"")));
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Read(valid.Replace("SchematicText", "UnrecognizedNativeType")));
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Read("<!DOCTYPE data SYSTEM 'file:///not-read'>" + valid));
        var bad = new SchematicLine { Type = (SchematicLineType)999 };
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Write(bad));
        var unknown = new SchematicSymbolChild { Item = new Any { TypeUrl = "type.googleapis.com/unknown.Native", Value = ByteString.Empty } };
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Write(unknown));
    }

    [TestMethod]
    public void SchemaRejectsUnknownDuplicateAndOutOfRangeFields()
    {
        XNamespace ns = SchematicDataXml.Namespace;
        string xml = SchematicDataXml.Write(new SchematicText { Id = new() { Value = "stable-id" } });
        var document = XDocument.Parse(xml);
        var entity = document.Root!.Elements().Single();
        entity.Add(new XElement(ns + "unsupported", "lost data"));
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Read(document.ToString()));
        document = XDocument.Parse(xml); entity = document.Root!.Elements().Single();
        entity.Add(new XElement(entity.Elements().Single()));
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Read(document.ToString()));
        string positioned = SchematicDataXml.Write(new SchematicText { Text = new() { Position = new() { XNm = 42 } } });
        Assert.ThrowsExactly<AutomationException>(() => SchematicDataXml.Read(positioned.Replace("42", "9223372036854775808")));
        StringAssert.Contains(SchematicDataXml.ExportSchema(), "xs:long");
    }

    private static IMessage Populate(MessageDescriptor descriptor, int depth)
    {
        if (descriptor == Any.Descriptor)
            return Any.Pack(new SchematicText { Text = new() { Text_ = "Nested object" } });
        var message = descriptor.Parser.ParseFrom(Array.Empty<byte>());
        var oneofs = new HashSet<OneofDescriptor>();
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (field.RealContainingOneof is { } oneof && !oneofs.Add(oneof)) continue;
            if (field.FieldType == FieldType.Message && depth == 0) continue;
            if (field.IsMap)
            {
                var map = (IDictionary)field.Accessor.GetValue(message);
                map.Add(Value(field.MessageType.Fields[1], depth - 1), Value(field.MessageType.Fields[2], depth - 1));
            }
            else if (field.IsRepeated)
            {
                var list = (IList)field.Accessor.GetValue(message);
                list.Add(Value(field, depth - 1)); list.Add(Value(field, depth - 1));
            }
            else field.Accessor.SetValue(message, Value(field, depth - 1));
        }
        return message;
    }

    private static object Value(FieldDescriptor field, int depth) => field.FieldType switch
    {
        FieldType.Message => Populate(field.MessageType, depth),
        FieldType.String => "  Native 電源\r\nvalue & < >  ",
        FieldType.Bytes => ByteString.CopyFrom(new byte[] { 0, 1, 2, 255 }),
        FieldType.Bool => true, FieldType.Float => 1.25f, FieldType.Double => -2.5d,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => -123,
        FieldType.UInt32 or FieldType.Fixed32 => uint.MaxValue,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => long.MinValue,
        FieldType.UInt64 or FieldType.Fixed64 => ulong.MaxValue,
        FieldType.Enum => System.Enum.ToObject(field.EnumType.ClrType, field.EnumType.Values.Last().Number),
        _ => throw new InvalidOperationException(field.FullName)
    };
}
