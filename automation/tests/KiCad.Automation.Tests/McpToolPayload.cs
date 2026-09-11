using System.Text.Json;

namespace KiCad.Automation.Tests;

internal static class McpToolPayload
{
    internal static JsonElement Object(JsonElement result)
    {
        if (result.TryGetProperty("isError", out var failed) && failed.GetBoolean())
            throw new InvalidDataException("The tool failed: " + result.GetRawText());
        if (result.TryGetProperty("structuredContent", out var structured))
            return structured.ValueKind == JsonValueKind.Object ? structured.Clone()
                : throw new InvalidDataException("The tool payload is not an object.");
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The tool has no payload.");
        var texts = content.EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text").ToArray();
        if (texts.Length != 1 || !texts[0].TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The tool has no unambiguous JSON text payload.");
        using var parsed = JsonDocument.Parse(text.GetString()!);
        return parsed.RootElement.ValueKind == JsonValueKind.Object ? parsed.RootElement.Clone()
            : throw new InvalidDataException("The tool JSON text is not an object.");
    }
}
