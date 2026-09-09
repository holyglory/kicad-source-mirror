using System.Runtime.InteropServices;
using System.Text.Json;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

public static class RuntimeInfoCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            if (args.Length != 1 || args[0] != "--runtime-info")
                throw new ArgumentException("Use --runtime-info without additional arguments.");
            token.ThrowIfCancellationRequested();
            string nngVersion = NngTransport.ProbeLibrary();
            token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "runtime_available", framework = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                operatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                runtimeIdentifier = RuntimeInformation.RuntimeIdentifier, nngVersion,
                nativeEditorContacted = false, crossPlatformReady = false
            }, json));
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException
            or OperationCanceledException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = error is OperationCanceledException ? "cancelled" : "failed",
                nativeEditorContacted = false, crossPlatformReady = false,
                error = new { kind = error.GetType().Name, message = error.InnerException?.Message ?? error.Message }
            }, json));
            return error is OperationCanceledException ? 2 : 1;
        }
        finally { await output.FlushAsync(CancellationToken.None); }
    }
}
