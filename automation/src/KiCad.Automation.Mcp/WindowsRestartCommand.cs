using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Mcp;

public sealed record WindowsRestartConfiguration(int SchemaVersion, WindowsUpdateHandoffRequest Request);

public static class WindowsRestartCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        bool acknowledged = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows restart requires native Windows.");
            if (args.Length != 3 || args[0] != "--restart-update" || args[1] != "--configuration" || !Path.IsPathFullyQualified(args[2]))
                throw new ArgumentException("Use --restart-update --configuration ABSOLUTE_REQUEST.");
            byte[] bytes;
            await using (var file = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length is < 1 or > 65536) throw new InvalidDataException("Invalid Windows restart request size.");
                bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token);
            }
            var configuration = Parse(bytes);
            var state = await WindowsUpdateHandoff.ExecuteAsync(configuration.Request, async waiting =>
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, state = waiting }, Json));
                await output.FlushAsync(CancellationToken.None);
                acknowledged = true;
            }, token);
            // After acknowledgment the old window can close its pipes. The
            // persistent journal, not a final stdout reply, owns completion.
            if (!acknowledged)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, state }, Json));
                await output.FlushAsync(CancellationToken.None);
            }
            return state.Status is "restarted" or "restored" ? 0 : state.Status == "cancelled_before_activation" ? 2 : 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException
            or UnauthorizedAccessException or Win32Exception or OperationCanceledException or PlatformNotSupportedException)
        {
            if (!acknowledged)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1,
                    status = error is OperationCanceledException ? "cancelled" : "failed", nativeEditorRestarted = false,
                    error = new { kind = error.GetType().Name, message = error.Message } }, Json));
                await output.FlushAsync(CancellationToken.None);
            }
            return error is OperationCanceledException ? 2 : 1;
        }
    }

    internal static WindowsRestartConfiguration Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65536) throw new InvalidDataException("Invalid Windows restart request size.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        CheckProperties(document.RootElement);
        var configuration = JsonSerializer.Deserialize<WindowsRestartConfiguration>(bytes, Json);
        if (configuration is null || configuration.SchemaVersion != 3 || configuration.Request is null)
            throw new InvalidDataException("A schema-version 3 Windows restart request is required.");
        return configuration;
    }
    private static void CheckProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Repeated Windows restart property."); CheckProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) CheckProperties(element);
    }
}
