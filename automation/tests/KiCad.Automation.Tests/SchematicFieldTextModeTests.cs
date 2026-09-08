using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicFieldTextModeTests
{
    [TestMethod]
    public void FieldsRejectUnsavableModesAcrossNativeOwnersAndPreserveInput()
    {
        Func<SchematicField, IMessage>[] owners =
        [
            f => new GlobalLabel { IntersheetRefsField = f },
            f => new LocalLabel { Fields = { f } },
            f => new HierarchicalLabel { Fields = { f } },
            f => new DirectiveLabel { Fields = { f } },
            f => new SheetSymbol { FilenameField = f },
            f => new SchematicSymbolInstance { ReferenceField = f },
            f => new SchematicSymbolInstance { Definition = new() { ValueField = f } },
            f => new SchematicCachedSymbol { Definition = new() { Items = { new SchematicSymbolChild { Item = Any.Pack(f) } } } }
        ];
        foreach (var owner in owners)
        {
            var field = new SchematicField { Name = "Note", Text = new()
                { Text_ = "Line one\nLine two", Attributes = new() { Multiline = true } } };
            var valid = owner(field);
            SchematicFieldTextModes.Validate(valid);
            SchematicFieldTextModes.Validate(Any.Pack(valid));
            field.Text.Attributes.Multiline = false;
            var invalid = owner(field);
            var bytes = invalid.ToByteArray();
            Assert.ThrowsExactly<AutomationException>(() => SchematicFieldTextModes.Validate(invalid));
            Assert.ThrowsExactly<AutomationException>(() => SchematicFieldTextModes.Validate(Any.Pack(invalid)));
            CollectionAssert.AreEqual(bytes, invalid.ToByteArray());
        }
    }

    [TestMethod]
    public void ALabelsOwnSingleLineModeDoesNotInvalidateItsMultilineCapableFields()
    {
        var label = new GlobalLabel { Text = new() { Text_ = "SIGNAL", Attributes = new() { Multiline = false } },
            IntersheetRefsField = new() { Text = new() { Attributes = new() { Multiline = true } } } };
        SchematicFieldTextModes.Validate(label);
        SchematicFieldTextModes.Validate(new SchematicText { Text = new()
            { Text_ = "Ordinary note", Attributes = new() { Multiline = false } } });
    }
}
