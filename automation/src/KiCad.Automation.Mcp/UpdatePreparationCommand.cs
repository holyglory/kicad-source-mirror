using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Mcp;

/// <summary>Finite native-helper mode in the same compiled executable as MCP.
/// This does not listen for editor control, install files into a live version,
/// or authorize restarting an editor.</summary>
public static class UpdatePreparationCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 8 };

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args.Length != 3 || args[0] is not ("--prepare-update" or "--check-update") || args[1] != "--configuration"
                || !Path.IsPathFullyQualified(args[2]))
                throw new ArgumentException("Use --check-update or --prepare-update, then --configuration and an absolute installed configuration path.");
            var configuration = await ReadConfigurationAsync(args[2], token);
            string runtime = (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
            {
                (true, _, Architecture.X64) => "linux-x64",
                (_, true, Architecture.Arm64) => "osx-arm64",
                (_, true, Architecture.X64) => "osx-x64",
                _ => throw new PlatformNotSupportedException("This updater runtime target is unsupported.")
            };
            if (configuration.Platform != runtime)
                throw new InvalidDataException("The installed updater configuration targets a different architecture.");
            byte[] publisherKey = Convert.FromBase64String(configuration.PublisherKeySpki);
            byte[] installed;
            await using (var file = new FileStream(configuration.InstalledEnvelope, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length is < 1 or > UpdateManifestCodec.MaximumEnvelopeBytes)
                    throw new InvalidDataException("Installed update receipt size is invalid.");
                installed = new byte[(int)file.Length];
                await file.ReadExactlyAsync(installed, token);
            }
            // Origin/key/configuration are supplied by the trusted installation;
            // a network response cannot replace them or request arbitrary paths.
            using var source = new UpdateDownloader(new Uri(configuration.Origin, UriKind.Absolute));
            var checker = new UpdateChecker(source, configuration.StateDirectory, publisherKey, installed,
                configuration.Channel, configuration.Platform, configuration.Format);
            string? expectedTarget = configuration.InstallationRoot is { } installationRoot
                ? LinuxUpdateActivation.InspectTarget(Path.Combine(installationRoot, "manager")) : null;
            await Emit(new { schemaVersion = 1, status = "checking", installationReady = false });
            var result = await checker.CheckAsync(token);
            if (args[0] == "--check-update" || result.Availability != UpdateAvailability.Available)
            {
                await Emit(new
                {
                    schemaVersion = 1,
                    status = result.Availability switch
                    {
                        UpdateAvailability.UpToDate => "up_to_date",
                        UpdateAvailability.Available => "available",
                        _ => "target_unavailable"
                    },
                    installationReady = false, manifestSha256 = result.Manifest.PayloadSha256,
                    version = result.Manifest.Release.Version, commit = result.Manifest.Release.Commit,
                    sequence = result.Manifest.Release.Sequence
                });
                return 0;
            }
            if (runtime != "linux-x64" || configuration.Format != "tar.gz")
            {
                await Emit(new { schemaVersion = 1, status = "preparation_unavailable", installationReady = false });
                return 0;
            }
            var progress = new TransferProgress(output);
            var download = await source.DownloadAsync(result.Manifest, runtime, configuration.Format,
                configuration.StagingDirectory, progress, token);
            if (configuration.InstallationRoot is { } managedRoot)
            {
                await Emit(new { schemaVersion = 1, status = "registering_candidate", installationReady = false });
                var candidate = await LinuxVerifiedInstallation.RegisterAsync(managedRoot, expectedTarget!, download.Path,
                    result.Manifest.CopyEnvelope(), token);
                await Emit(new
                {
                    schemaVersion = 1, status = "candidate_registered", installationReady = false,
                    directory = candidate.Version.VersionDirectory, manifestSha256 = candidate.Version.ManifestSha256,
                    version = result.Manifest.Release.Version, commit = candidate.Version.Commit,
                    expectedTarget = candidate.ExpectedTarget, reused = candidate.Reused
                });
                return 0;
            }
            await Emit(new { schemaVersion = 1, status = "staging", installationReady = false });
            var staged = await LinuxUpdateStager.StageAsync(result.Manifest, download, configuration.StagingDirectory, token);
            await Emit(new { schemaVersion = 1, status = "checking_native_identity", installationReady = false });
            var native = await LinuxUpdateRuntime.CheckAsync(result.Manifest, staged, configuration.StagingDirectory, token);
            await Emit(new
            {
                schemaVersion = 1, status = "archive_staged", installationReady = false,
                directory = staged.Directory, manifestSha256 = staged.ManifestSha256,
                version = result.Manifest.Release.Version, commit = result.Manifest.Release.Commit,
                nativeIdentityVerified = true, nativeCommit = native.Commit
            });
            return 0;
        }
        catch (OperationCanceledException)
        {
            await Emit(new { schemaVersion = 1, status = "cancelled", installationReady = false });
            return 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or FormatException or CryptographicException or HttpRequestException or PlatformNotSupportedException
            or Win32Exception)
        {
            await Emit(new { schemaVersion = 1, status = "failed", installationReady = false,
                error = new { kind = error.GetType().Name, message = error.Message } });
            return 1;
        }

        async Task Emit<T>(T value)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, Json));
            await output.FlushAsync();
        }
    }

    private static async Task<UpdatePreparationConfiguration> ReadConfigurationAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 64 * 1024) throw new InvalidDataException("Invalid updater configuration size.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() != 1))
            throw new InvalidDataException("Invalid or repeated updater configuration property.");
        var value = JsonSerializer.Deserialize<UpdatePreparationConfiguration>(bytes, Json)
            ?? throw new InvalidDataException("Updater configuration is missing.");
        if (value.SchemaVersion != 1 || string.IsNullOrEmpty(value.PublisherKeySpki)
            || !Uri.TryCreate(value.Origin, UriKind.Absolute, out _)
            || string.IsNullOrEmpty(value.InstalledEnvelope) || !Path.IsPathFullyQualified(value.InstalledEnvelope)
            || string.IsNullOrEmpty(value.StateDirectory) || !Path.IsPathFullyQualified(value.StateDirectory)
            || !Directory.Exists(value.StateDirectory)
            || string.IsNullOrEmpty(value.StagingDirectory) || !Path.IsPathFullyQualified(value.StagingDirectory)
            || !Directory.Exists(value.StagingDirectory))
            throw new InvalidDataException("Updater configuration requires an installed identity and absolute private state/staging paths.");
        if (value.InstallationRoot is { } root && (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)))
            throw new InvalidDataException("The managed installation root is missing or not absolute.");
        return value;
    }

    private sealed class TransferProgress(TextWriter output) : IProgress<UpdateTransferProgress>
    {
        private long nextReport;
        public void Report(UpdateTransferProgress value)
        {
            if (value.ReceivedBytes < nextReport && value.ReceivedBytes != value.ExpectedBytes) return;
            nextReport = value.ReceivedBytes + 16 * 1024 * 1024;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "downloading", installationReady = false,
                receivedBytes = value.ReceivedBytes, expectedBytes = value.ExpectedBytes
            }, Json));
            output.Flush();
        }
    }
}
