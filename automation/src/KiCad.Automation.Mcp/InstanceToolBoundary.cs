using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Mcp;

internal static class InstanceToolBoundary
{
    // Preserve success schemas and cancellation. Only intentional domain/native
    // errors are public; unexpected failures retain the SDK's generic response.
    internal static async Task<CallToolResult> Run<T>(Func<Task<T>> operation)
    {
        try
        {
            T value = await operation();
            if (value is string text) return new() { Content = [new TextContentBlock { Text = text }] };
            var json = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
        catch (AutomationException error) { return Public(error.Code, error.Message); }
        catch (NativeApiException error) { return Public("native_status_" + error.Status, error.Message); }
        catch (ArgumentException error) { return Public("invalid_argument", error.Message); }
        catch (NngException) { return Public("native_transport_unavailable", "The explicit native endpoint could not be reached. Inspect the saved instance and process before retrying; do not launch a replacement blindly."); }
    }

    private static CallToolResult Public(string code, string message)
    {
        var json = JsonSerializer.SerializeToElement(new { code, message });
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
