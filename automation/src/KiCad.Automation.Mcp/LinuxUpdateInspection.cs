using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

public sealed record UpdateInspection(string Status, Guid OperationId, DateTimeOffset ObservedAtUtc,
    string? JournalStatus = null, string? PreviousManifestSha256 = null, string? CurrentSelection = null,
    string? OriginalProcessStatus = null, string? ReplacementProcessStatus = null,
    LinuxProcessIdentity? ObservedReplacement = null, string? NativeEpoch = null,
    bool AutomaticRecoveryAvailable = false);

/// <summary>Observes an existing handoff under its existing ownership lock.
/// Never starts/stops processes, changes selection, or rewrites the journal.
/// A report is a point-in-time observation, not authorization to relaunch.</summary>
public static class LinuxUpdateInspection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public static async Task<UpdateInspection> InspectAsync(string installationRoot, Guid operationId,
        CancellationToken token = default) =>
        (await InspectCoreAsync(installationRoot, operationId, token)) with { ObservedAtUtc = DateTimeOffset.UtcNow };

    private static async Task<UpdateInspection> InspectCoreAsync(string installationRoot, Guid operationId,
        CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Update inspection requires Linux.");
        if (operationId == Guid.Empty || !Path.IsPathFullyQualified(installationRoot))
            throw new ArgumentException("Select an absolute installation and non-empty operation ID.");
        token.ThrowIfCancellationRequested();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot));
        string journal = Path.Combine(root, "handovers", operationId.ToString("D"));
        var report = new UpdateInspection("journal_missing", operationId, DateTimeOffset.UtcNow);
        if (!Directory.Exists(journal)) return report;
        if (new DirectoryInfo(journal).LinkTarget is not null)
            throw new InvalidDataException("The operation directory is linked rather than owned by this installation.");
        string lockPath = Path.Combine(journal, "operation.lock");
        if (!File.Exists(lockPath)) return report with { Status = "journal_incomplete" };
        if (new FileInfo(lockPath).LinkTarget is not null) throw new InvalidDataException("The operation lock is linked.");
        FileStream ownership;
        try { ownership = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
        // IO failure may be a held lock or a filesystem problem. Do not infer
        // that a helper is alive merely because exclusive observation failed.
        catch (IOException) { return report with { Status = "operation_unavailable" }; }
        using (ownership)
        {
            string intentPath = Path.Combine(journal, "intent.json");
            if (!File.Exists(intentPath)) return report with { Status = "legacy_journal_unverifiable" };
            if (!File.Exists(Path.Combine(journal, "request.json")) || !File.Exists(Path.Combine(journal, "state.json")))
                return report with { Status = "journal_incomplete" };
            var intent = await ReadAsync<UpdateHandoffIntent>(intentPath, token);
            var request = await ReadAsync<LinuxUpdateHandoffRequest>(Path.Combine(journal, "request.json"), token);
            var state = await ReadAsync<UpdateHandoffState>(Path.Combine(journal, "state.json"), token);
            Validate(root, journal, operationId, intent, request, state);
            string oldExecutable = Path.Combine(intent.PreviousVersion.VersionDirectory, "runtime/bin/kicad");
            var previous = await LinuxVerifiedInstallation.InspectExecutableVersionAsync(root, oldExecutable, token);
            if (previous != intent.PreviousVersion)
                throw new InvalidDataException("The previous editor registration no longer matches the saved intent.");
            string selection = LinuxUpdateActivation.InspectTarget(Path.Combine(root, "manager"));
            string original = ObserveProcess(request.OldProcess, oldExecutable);
            report = report with { JournalStatus = state.Status, PreviousManifestSha256 = previous.ManifestSha256,
                CurrentSelection = selection, OriginalProcessStatus = original };
            if (state.ProcessId is null)
                return report with { Status = original == "live" ? "original_running"
                    : state.Status is "waiting_for_exit" or "cancelled_before_activation" or "activating" or "activation_failed"
                        ? "no_replacement_recorded" : "launch_outcome_unknown" };
            if (state.ProcessIdentity is null)
                return report with { Status = "replacement_identity_unavailable", ReplacementProcessStatus = "unknown" };
            bool restored = state.Endpoint == "ipc://" + request.SocketPath + ".r";
            string executable = restored ? oldExecutable
                : Path.Combine(root, "versions", request.ManifestSha256, "payload/runtime/bin/kicad");
            _ = await LinuxVerifiedInstallation.InspectExecutableVersionAsync(root, executable, token);
            string replacement = ObserveProcess(state.ProcessIdentity, executable);
            report = report with { ReplacementProcessStatus = replacement };
            if (replacement != "live") return report with { Status = "replacement_" + replacement };
            report = report with { ObservedReplacement = state.ProcessIdentity };
            if (original == "live") return report with { Status = "conflicting_live_processes" };
            try
            {
                var client = new NativeClient(new NngTransport(), state.Endpoint!, state.NativeEpoch);
                var session = await client.HandshakeAsync(token);
                if (session.InstanceId != request.InstanceId.ToString("D") || session.ProjectPath != request.ProjectPath)
                    return report with { Status = "native_identity_mismatch" };
                if (ObserveProcess(state.ProcessIdentity, executable) != "live")
                    return report with { Status = "replacement_changed_during_observation" };
                return report with { Status = "replacement_running", NativeEpoch = session.Epoch };
            }
            catch (NngException) { return report with { Status = "native_endpoint_unavailable" }; }
            catch (NativeApiException error) when (error.Status is 4 or 7)
            { return report with { Status = "native_startup_pending" }; }
        }
    }

    private static string ObserveProcess(LinuxProcessIdentity expected, string executable)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            if (process.HasExited) return "exited";
            if (LinuxProcessIdentity.Read(process.Id) != expected) return "identity_changed";
            if (process.MainModule?.FileName != executable) return "executable_mismatch";
            if (process.HasExited) return "exited";
            return LinuxProcessIdentity.Read(process.Id) == expected ? "live" : "identity_changed";
        }
        catch (ArgumentException) { return "exited"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        { return "unavailable"; }
    }

    private static void Validate(string root, string journal, Guid operation, UpdateHandoffIntent intent,
        LinuxUpdateHandoffRequest request, UpdateHandoffState state)
    {
        if (intent.SchemaVersion != 1 || intent.Request != request || intent.PreviousVersion is null
            || intent.PreparedAtUtc == default || request.OperationId != operation
            || !Path.IsPathFullyQualified(request.InstallationRoot)
            || Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallationRoot)) != root
            || request.OldProcess is null || request.OldProcess.ProcessId <= 0 || request.OldProcess.BootId == Guid.Empty
            || request.InstanceId == Guid.Empty || request.ProjectPath is null
            || request.ProjectPath.Length != 0 && (!Path.IsPathFullyQualified(request.ProjectPath)
                || Path.GetExtension(request.ProjectPath) != ".kicad_pro")
            || !Path.IsPathFullyQualified(request.SocketPath) || request.SocketPath.Length > 80
            || !Digest(request.ManifestSha256) || state.JournalDirectory != journal
            || intent.PreviousVersion.Root != root || !Digest(intent.PreviousVersion.ManifestSha256))
            throw new InvalidDataException("The saved handoff does not match this exact installation and operation.");
        if (state.Status is not ("waiting_for_exit" or "cancelled_before_activation" or "activating" or "activation_failed"
            or "launching" or "launch_failed" or "awaiting_native" or "rolling_back" or "restarted" or "restored" or "reconciliation_required"))
            throw new InvalidDataException("The saved handoff status is unsupported.");
        if (state.ProcessId is null && (state.ProcessIdentity is not null || state.Endpoint is not null)
            || state.ProcessId is <= 0 || state.ProcessIdentity is not null
                && (state.ProcessIdentity.ProcessId != state.ProcessId || state.ProcessIdentity.BootId == Guid.Empty)
            || state.ProcessId is not null && state.Endpoint != "ipc://" + request.SocketPath
                && state.Endpoint != "ipc://" + request.SocketPath + ".r")
            throw new InvalidDataException("The saved replacement process and endpoint identities are inconsistent.");
    }

    private static bool Digest(string? value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<T> ReadAsync<T>(string path, CancellationToken token) where T : class
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 or > 65536)
            throw new InvalidDataException("Update journal input must be a bounded regular file.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 65536) throw new InvalidDataException("Update journal input changed size.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Update journal input is missing.");
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new InvalidDataException("Repeated update journal property.");
                RejectDuplicates(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
