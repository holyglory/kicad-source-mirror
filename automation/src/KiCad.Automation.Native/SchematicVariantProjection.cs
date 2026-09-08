using System.Collections;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// Descriptions projected onto symbol/sheet observations are not another owner
// of the project registry. Work on decoded copies, never the caller's messages.
internal static class SchematicVariantProjection
{
    private static IEnumerable<IMessage> Variants(IMessage item)
    {
        IMessage? container = item switch
        {
            SchematicSymbolInstance symbol => symbol.Variants,
            SheetSymbol sheet => sheet.Variants,
            _ => null
        };
        if (container is null) return [];
        return ((IEnumerable)container.Descriptor.FindFieldByName("variants").Accessor.GetValue(container)).Cast<IMessage>();
    }

    public static void ValidateAndStrip(IMessage item, SchematicMetadata metadata, SchematicMetadata? prior = null)
    {
        foreach (var variant in Variants(item))
        {
            string name = (string)variant.Descriptor.FindFieldByName("name").Accessor.GetValue(variant);
            var description = variant.Descriptor.FindFieldByName("description").Accessor;
            if (description.HasValue(variant))
            {
                string value = (string)description.GetValue(variant);
                string expected = metadata.VariantDescriptions.TryGetValue(name, out var text) ? text : "";
                string? old = prior is null ? null : prior.VariantDescriptions.TryGetValue(name, out var oldText) ? oldText : "";
                if (value != expected && value != old)
                    throw new AutomationException("unsupported_schematic_delta",
                        "Projected variant descriptions must agree with the explicit project registry or its unchanged prior value.");
            }
            description.Clear(variant);
        }
    }

    public static bool Equivalent(IMessage? left, IMessage? right)
    {
        if (left is null || right is null) return Equals(left, right);
        IMessage Copy(IMessage value)
        {
            if (value is not SchematicSymbolInstance and not SheetSymbol) return value;
            var copy = value.Descriptor.Parser.ParseFrom(value.ToByteArray());
            foreach (var variant in Variants(copy)) variant.Descriptor.FindFieldByName("description").Accessor.Clear(variant);
            return copy;
        }
        return Copy(left).Equals(Copy(right));
    }

    public static IMessage Reproject(IMessage item, SchematicMetadata metadata)
    {
        if (item is not SchematicSymbolInstance and not SheetSymbol) return item;
        var copy = item.Descriptor.Parser.ParseFrom(item.ToByteArray());
        foreach (var variant in Variants(copy))
        {
            string name = (string)variant.Descriptor.FindFieldByName("name").Accessor.GetValue(variant);
            var description = variant.Descriptor.FindFieldByName("description").Accessor;
            if (description.HasValue(variant) || metadata.VariantDescriptions.ContainsKey(name))
                description.SetValue(variant, metadata.VariantDescriptions.TryGetValue(name, out var text) ? text : "");
        }
        return copy;
    }

    public static void Reproject(SchematicScreenData state)
    {
        var items = SchematicItemDelta.Index(state.Items).Values
            .Select(item => Google.Protobuf.WellKnownTypes.Any.Pack(Reproject(item, state.Metadata))).ToArray();
        state.Items.Clear(); state.Items.Add(items);
    }
}
