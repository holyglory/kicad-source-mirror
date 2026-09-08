using Google.Protobuf;
using KiCad.Automation.Native;
using System.Text.Json;

namespace KiCad.Automation.Tests;

// Bounded fixture diagnostics. Full snapshots remain in the evidence archive;
// assertions report exact fields rather than dumping complete designs to logs.
internal static class NativeSnapshotDifference
{
    public static string Describe(IMessage expected, IMessage actual)
    {
        using var left = JsonDocument.Parse(SchematicJson.Formatter.Format(expected));
        using var right = JsonDocument.Parse(SchematicJson.Formatter.Format(actual));
        var differences = new List<string>();
        Visit(left.RootElement, right.RootElement, "$", differences);
        return string.Join(Environment.NewLine, differences);
    }

    private static void Visit(JsonElement expected, JsonElement actual, string path, List<string> output)
    {
        if (output.Count >= 8) return;
        if (expected.ValueKind != actual.ValueKind)
        {
            output.Add($"{path}: {expected.ValueKind} -> {actual.ValueKind}");
            return;
        }
        if (expected.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
                if (actual.TryGetProperty(property.Name, out var other))
                    Visit(property.Value, other, path + "." + property.Name, output);
                else if (output.Count < 8) output.Add(path + "." + property.Name + ": missing after reload");
            foreach (var property in actual.EnumerateObject())
                if (output.Count < 8 && !expected.TryGetProperty(property.Name, out _))
                    output.Add(path + "." + property.Name + ": appeared after reload");
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength())
                output.Add($"{path}.length: {expected.GetArrayLength()} -> {actual.GetArrayLength()}");
            for (int index = 0; index < Math.Min(expected.GetArrayLength(), actual.GetArrayLength()); ++index)
                Visit(expected[index], actual[index], $"{path}[{index}]", output);
        }
        else if (expected.GetRawText() != actual.GetRawText())
        {
            static string Clip(JsonElement value)
            {
                var text = value.GetRawText();
                return text.Length <= 160 ? text : text[..160] + "…";
            }
            output.Add($"{path}: {Clip(expected)} -> {Clip(actual)}");
        }
    }
}
