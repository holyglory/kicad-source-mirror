using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kiapi.Schematic.Types;

namespace KiCad.Automation.Native;

/// <summary>Typed schematic JSON, including native items carried in protobuf Any.</summary>
public static class SchematicJson
{
    private static readonly TypeRegistry Types = TypeRegistry.FromFiles(SchematicText.Descriptor.File);
    public static JsonFormatter Formatter { get; } = new(JsonFormatter.Settings.Default.WithTypeRegistry(Types));
    public static JsonParser Parser { get; } = new(JsonParser.Settings.Default.WithTypeRegistry(Types));
}
