using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiCad.Automation.Mcp;

public sealed record MacRestartConfiguration(int SchemaVersion, MacUpdateHandoffRequest Request);

public static class MacRestartCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        bool handedOff = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (args.Length != 3 || args[0] != "--restart-update" || args[1] != "--configuration" || !Path.IsPathFullyQualified(args[2]))
                throw new ArgumentException("Use --restart-update --configuration followed by an absolute restart request path.");
            byte[] bytes;
            await using (var file = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length is < 1 or > 64 * 1024) throw new InvalidDataException("Restart request size is invalid.");
                bytes = new byte[(int)file.Length];
                await file.ReadExactlyAsync(bytes, token);
            }
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            var configuration = JsonSerializer.Deserialize<MacRestartConfiguration>(bytes, Json);
            if (configuration is null || configuration.SchemaVersion != 2 || configuration.Request is null)
                throw new InvalidDataException("A versioned restart request is required.");
            var result = await MacUpdateHandoff.ExecuteAsync(configuration.Request, async state =>
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, state }, Json));
                await output.FlushAsync(CancellationToken.None);
                handedOff = true;
            }, token);
            // Once the old manager has permission to close, its pipes are not
            // reliable. The journal is authoritative; never depend on sending a
            // final message back to the window that has exited.
            if (!handedOff)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, state = result }, Json));
                await output.FlushAsync(CancellationToken.None);
            }
            return result.Status is "restarted" or "restored" ? 0 : result.Status == "cancelled_before_activation" ? 2 : 1;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or Win32Exception or OperationCanceledException)
        {
            if (!handedOff)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, status = error is OperationCanceledException ? "cancelled" : "failed",
                    nativeEditorRestarted = false, error = new { kind = error.GetType().Name, message = error.Message }
                }, Json));
                await output.FlushAsync(CancellationToken.None);
            }
            return error is OperationCanceledException ? 2 : 1;
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Repeated restart request property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) RejectDuplicates(element);
    }
}
