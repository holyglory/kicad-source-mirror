using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Mcp;

public static class LinuxUpdateInspectionCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            if (args.Length != 5 || args[0] != "--inspect-update" || args[1] != "--installation"
                || args[3] != "--operation" || !Guid.TryParseExact(args[4], "D", out var operation))
                throw new ArgumentException("Use --inspect-update --installation ABSOLUTE_ROOT --operation UUID.");
            var result = await LinuxUpdateInspection.InspectAsync(args[2], operation, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, result }, json));
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException or OperationCanceledException
            or KiCad.Automation.Model.AutomationException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1,
                status = error is OperationCanceledException ? "cancelled" : "failed", automaticRecoveryAvailable = false,
                error = new { kind = error.GetType().Name, message = error.Message } }, json));
            return error is OperationCanceledException ? 2 : 1;
        }
        finally { await output.FlushAsync(CancellationToken.None); }
    }
}
