using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Mcp;

public sealed record LinuxInstallRequest(int SchemaVersion, string InstallationRoot, string ArchivePath,
    string EnvelopePath, string Origin, string Channel);

public static class LinuxInstallCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args.Length != 5 || args[0] != "--install-package" || args[1] != "--configuration" || args[3] != "--publisher-key"
                || !Path.IsPathFullyQualified(args[2]) || !Path.IsPathFullyQualified(args[4]))
                throw new ArgumentException("Use --install-package --configuration ABSOLUTE_REQUEST --publisher-key ABSOLUTE_TRUSTED_PUBLIC_KEY.");
            byte[] bytes = await ReadAsync(args[2], 64 * 1024, token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() != 1))
                throw new InvalidDataException("Invalid or repeated installation request property.");
            var request = JsonSerializer.Deserialize<LinuxInstallRequest>(bytes, Json)
                ?? throw new InvalidDataException("Installation request is missing.");
            if (request.SchemaVersion != 1 || string.IsNullOrEmpty(request.InstallationRoot)
                || !Path.IsPathFullyQualified(request.InstallationRoot) || string.IsNullOrEmpty(request.ArchivePath)
                || !Path.IsPathFullyQualified(request.ArchivePath) || string.IsNullOrEmpty(request.EnvelopePath)
                || !Path.IsPathFullyQualified(request.EnvelopePath) || !Uri.TryCreate(request.Origin, UriKind.Absolute, out var origin))
                throw new InvalidDataException("Installation request requires absolute paths and a publisher origin.");
            byte[] publisherKey = await ReadAsync(args[4], 4096, token);
            byte[] envelope = await ReadAsync(request.EnvelopePath, UpdateManifestCodec.MaximumEnvelopeBytes, token);
            await Emit(new { schemaVersion = 1, status = "verifying_installation", automaticUpdatingQualified = false });
            var result = await LinuxVerifiedInstallation.InstallAsync(request.InstallationRoot, request.ArchivePath,
                envelope, publisherKey, origin, request.Channel, token);
            await Emit(new { schemaVersion = 1, status = "installed", result,
                automaticUpdatingQualified = false, nativeEditorRestarted = false });
            return 0;
        }
        catch (OperationCanceledException)
        {
            await Emit(new { schemaVersion = 1, status = "cancelled", automaticUpdatingQualified = false });
            return 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or FormatException or CryptographicException or PlatformNotSupportedException or Win32Exception)
        {
            await Emit(new { schemaVersion = 1, status = "failed", automaticUpdatingQualified = false,
                error = new { kind = error.GetType().Name, message = error.Message } });
            return 1;
        }

        async Task Emit<T>(T value)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, Json));
            await output.FlushAsync(CancellationToken.None); // terminal cancellation/failure must still be emitted
        }
    }

    private static async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("Installation input size is invalid.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        return bytes;
    }
}
