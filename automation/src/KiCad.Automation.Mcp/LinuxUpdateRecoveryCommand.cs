using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Mcp;

public static class LinuxUpdateRecoveryCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        bool recoveryEntered = false;
        try
        {
            if (args.Length != 7 || args[0] != "--recover-update" || args[1] != "--installation" || args[3] != "--operation"
                || args[5] != "--attempt" || !Guid.TryParseExact(args[4], "D", out var operation)
                || !Guid.TryParseExact(args[6], "D", out var attempt))
                throw new ArgumentException("Use --recover-update --installation ABSOLUTE_ROOT --operation UUID --attempt UUID.");
            recoveryEntered = true;
            var result = await LinuxUpdateRecovery.RecoverAsync(args[2], operation, attempt, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, result }, json));
            return result.Status is "restored" or "original_running" or "attempt_already_recorded" ? 0
                : result.Status == "recovery_cancelled" ? 2 : 1;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException or OperationCanceledException
            or KiCad.Automation.Model.AutomationException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1,
                status = error is OperationCanceledException ? "cancelled" : "failed",
                nativeEditorRestarted = recoveryEntered ? (bool?)null : false,
                launchOutcome = recoveryEntered ? "unverified_inspect_journal" : "not_started",
                error = new { kind = error.GetType().Name, message = error.Message } }, json));
            return error is OperationCanceledException ? 2 : 1;
        }
        finally { await output.FlushAsync(CancellationToken.None); }
    }
}
