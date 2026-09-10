using System.Text.Json;

namespace KiCad.Automation.Mcp;

public static class WindowsUpdateInspectionCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args.Length != 5 || args[0] != "--inspect-update" || args[1] != "--installation" || args[3] != "--operation"
                || !Guid.TryParseExact(args[4], "D", out var operation) || operation == Guid.Empty)
                throw new ArgumentException("Use --inspect-update --installation ABSOLUTE_ROOT --operation UUID.");
            var result = await WindowsUpdateInspection.InspectAsync(args[2], operation, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, result }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException
            or UnauthorizedAccessException or PlatformNotSupportedException or OperationCanceledException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1,
                status = error is OperationCanceledException ? "cancelled" : "failed", automaticRecoveryAvailable = false,
                error = new { kind = error.GetType().Name, message = error.Message } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return error is OperationCanceledException ? 2 : 1;
        }
        finally { await output.FlushAsync(CancellationToken.None); }
    }
}
