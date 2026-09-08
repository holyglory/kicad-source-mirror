using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// Fields reload in multiline mode even when their current text is one line.
// This is distinct from labels, whose own text must remain single-line.
internal static class SchematicFieldTextModes
{
    private static readonly TypeRegistry Types = TypeRegistry.FromFiles(SchematicField.Descriptor.File);

    internal static void Validate(IMessage message)
    {
        if (message is SchematicField field)
        {
            if (field.Text?.Attributes?.Multiline != true)
                throw new AutomationException("unsupported_schematic_delta",
                    "Schematic fields require native multiline text mode for save/reopen fidelity.");
            return;
        }
        if (message is Any any)
        {
            string type = any.TypeUrl[(any.TypeUrl.LastIndexOf('/') + 1)..];
            var descriptor = Types.Find(type) ?? throw new AutomationException("unsupported_schematic_delta",
                "Unsupported embedded schematic field container: " + type);
            Validate(descriptor.Parser.ParseFrom(any.Value));
            return;
        }
        foreach (var property in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (property.FieldType != FieldType.Message) continue;
            object value = property.Accessor.GetValue(message);
            if (property.IsMap)
            {
                foreach (object entry in ((IDictionary)value).Values)
                    if (entry is IMessage nested) Validate(nested);
            }
            else if (property.IsRepeated)
            {
                foreach (IMessage nested in (IEnumerable)value) Validate(nested);
            }
            else if (value is IMessage nested) Validate(nested);
        }
    }
}
