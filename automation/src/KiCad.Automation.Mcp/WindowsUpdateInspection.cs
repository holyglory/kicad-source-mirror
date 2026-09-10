using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

public sealed record WindowsUpdateInspectionResult(string Status, Guid OperationId, DateTimeOffset ObservedAtUtc,
    string? JournalStatus = null, string? PreviousManifestSha256 = null, string? CurrentSelection = null,
    string? OriginalProcessStatus = null, string? ReplacementProcessStatus = null,
    WindowsProcessIdentity? ObservedReplacement = null, string? NativeEpoch = null, bool AutomaticRecoveryAvailable = false);
internal sealed record WindowsUpdateJournalSnapshot(WindowsUpdateHandoffIntent Intent, WindowsUpdateHandoffState State,
    VerifiedWindowsVersion PreviousVersion, WindowsUpdateRecoveryClaim? Recovery);

/// <summary>Read an existing handoff without changing files, selection or processes.</summary>
public static class WindowsUpdateInspection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 16 };

    public static async Task<WindowsUpdateInspectionResult> InspectAsync(string installationRoot, Guid operationId, CancellationToken token = default)
        => (await InspectCoreAsync(installationRoot, operationId, token)) with { ObservedAtUtc = DateTimeOffset.UtcNow };

    private static async Task<WindowsUpdateInspectionResult> InspectCoreAsync(string installationRoot, Guid operationId, CancellationToken token)
    {
        string root = Root(installationRoot, operationId), journal = Path.Combine(root, "handovers", operationId.ToString("D"));
        token.ThrowIfCancellationRequested();
        var result = new WindowsUpdateInspectionResult("journal_missing", operationId, DateTimeOffset.UtcNow);
        if (!Directory.Exists(journal)) return result;
        Ordinary(root); Ordinary(Path.Combine(root, "handovers"));
        Ordinary(journal);
        string lockPath = Path.Combine(journal, "operation.lock");
        if (!File.Exists(lockPath)) return result with { Status = "journal_incomplete" };
        Ordinary(lockPath);
        FileStream ownership;
        try { ownership = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { return result with { Status = "operation_unavailable" }; }
        using (ownership)
        {
            if (!File.Exists(Path.Combine(journal, "intent.json"))) return result with { Status = "legacy_journal_unverifiable" };
            if (!File.Exists(Path.Combine(journal, "request.json")) || !File.Exists(Path.Combine(journal, "state.json")))
                return result with { Status = "journal_incomplete" };
            var snapshot = await ReadSnapshotAsync(root, journal, operationId, token);
            var request = snapshot.Intent.Request; var state = snapshot.State;
            string original = WindowsObservedProcess.Observe(request.OldProcess, snapshot.PreviousVersion.NativeExecutable);
            result = result with { ObservedAtUtc = DateTimeOffset.UtcNow, JournalStatus = state.Status,
                PreviousManifestSha256 = snapshot.PreviousVersion.ManifestSha256, CurrentSelection = WindowsVerifiedVersions.InspectSelectionId(root),
                OriginalProcessStatus = original };
            if (state.ProcessId is null)
                return result with { Status = original == "live" ? "original_running"
                    : state.Status is "waiting_for_exit" or "cancelled_before_activation" or "activating" or "activation_failed"
                        ? "no_replacement_recorded" : "launch_outcome_unknown" };
            if (state.ProcessIdentity is null) return result with { Status = "replacement_identity_unavailable", ReplacementProcessStatus = "unknown" };
            bool restored = state.Endpoint == Endpoint(request.SocketPath + ".r")
                || snapshot.Recovery is { } recovery && state.Endpoint == Endpoint(recovery.SocketPath);
            string executable = restored ? snapshot.PreviousVersion.NativeExecutable
                : Path.Combine(root, "versions", request.ManifestSha256, "payload", "bin", "kicad.exe");
            _ = await WindowsVerifiedVersions.InspectExecutableAsync(root, executable, token);
            string replacement = WindowsObservedProcess.Observe(state.ProcessIdentity, executable);
            result = result with { ReplacementProcessStatus = replacement };
            if (replacement != "live") return result with { Status = "replacement_" + replacement };
            result = result with { ObservedReplacement = state.ProcessIdentity };
            if (original == "live") return result with { Status = "conflicting_live_processes" };
            try
            {
                var client = new NativeClient(new NngTransport(), state.Endpoint!, state.NativeEpoch);
                var session = await client.HandshakeAsync(token);
                if (session.InstanceId != request.InstanceId.ToString("D") || session.ProjectPath != request.ProjectPath)
                    return result with { Status = "native_identity_mismatch" };
                if (WindowsObservedProcess.Observe(state.ProcessIdentity, executable) != "live")
                    return result with { Status = "replacement_changed_during_observation" };
                return result with { Status = "replacement_running", NativeEpoch = session.Epoch };
            }
            catch (NngException) { return result with { Status = "native_endpoint_unavailable" }; }
            catch (NativeApiException error) when (error.Status is 4 or 7) { return result with { Status = "native_startup_pending" }; }
            catch (KiCad.Automation.Model.AutomationException error)
            { return result with { Status = error.Code == "instance_changed" ? "native_identity_mismatch" : "native_protocol_error" }; }
        }
    }

    internal static async Task<WindowsUpdateJournalSnapshot> ReadSnapshotAsync(string root, string journal, Guid operation, CancellationToken token)
    {
        var intent = await ReadAsync<WindowsUpdateHandoffIntent>(Path.Combine(journal, "intent.json"), token);
        var request = await ReadAsync<WindowsUpdateHandoffRequest>(Path.Combine(journal, "request.json"), token);
        var state = await ReadAsync<WindowsUpdateHandoffState>(Path.Combine(journal, "state.json"), token);
        WindowsUpdateRecoveryClaim? recovery = null;
        if (File.Exists(Path.Combine(journal, "recovery.json")))
        {
            recovery = await ReadAsync<WindowsUpdateRecoveryClaim>(Path.Combine(journal, "recovery.json"), token);
        if (recovery.SchemaVersion != 1 || recovery.OperationId != operation || recovery.AttemptId == Guid.Empty || recovery.StartedAtUtc == default
                || !Path.IsPathFullyQualified(recovery.SocketPath)) throw new InvalidDataException("Invalid Windows recovery claim.");
            string prefix = Path.Combine(journal, "recovery-" + recovery.AttemptId.ToString("N"));
            if (recovery != await ReadAsync<WindowsUpdateRecoveryClaim>(prefix + ".json", token))
                throw new InvalidDataException("The Windows recovery pointer differs from its immutable claim.");
            state = await ReadAsync<WindowsUpdateHandoffState>(prefix + ".state.json", token);
        }
        if (intent.SchemaVersion != 1 || intent.PreparedAtUtc == default || intent.Request != request || intent.PreviousVersion is null
            || request.OperationId != operation || request.InstanceId == Guid.Empty || request.OldProcess is null
            || !SamePath(root, request.InstallationRoot) || !SamePath(journal, state.JournalDirectory)
            || request.ProjectPath is null || (request.ProjectPath.Length != 0 && (!Path.IsPathFullyQualified(request.ProjectPath)
                || Path.GetExtension(request.ProjectPath) != ".kicad_pro"))
            || !Path.IsPathFullyQualified(request.SocketPath) || !Guid.TryParseExact(request.ExpectedSelectionId, "D", out var selected)
            || selected == Guid.Empty || request.ManifestSha256 is not { Length: 64 } || !request.ManifestSha256.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException("Windows handoff identity or location does not match the requested operation.");
        if (!SamePath(root, intent.PreviousVersion.Root) || string.IsNullOrEmpty(intent.PreviousVersion.VersionDirectory)
            || !Path.IsPathFullyQualified(intent.PreviousVersion.VersionDirectory))
            throw new InvalidDataException("The saved previous Windows version is outside the requested installation.");
        request.OldProcess.Validate(); UpdateLaunchEnvironment.Validate(intent.LaunchEnvironment);
        string[] states = ["waiting_for_exit", "cancelled_before_activation", "activating", "activation_failed", "launching", "awaiting_native",
            "restarted", "restored", "restoring", "rolling_back", "launch_failed", "reconciliation_required", "recovering_previous", "recovery_cancelled"];
        if (!states.Contains(state.Status, StringComparer.Ordinal) || state.ProcessId is <= 0
            || (state.ProcessIdentity is not null && state.ProcessId != state.ProcessIdentity.ProcessId)
            || (state.ProcessId is not null && state.Endpoint is null)
            || (state.ProcessId is null && (state.ProcessIdentity is not null || state.Endpoint is not null || state.NativeEpoch is not null)))
            throw new InvalidDataException("Invalid Windows handoff process state.");
        if (state.ProcessIdentity is not null) state.ProcessIdentity.Validate();
        if (state.Endpoint is not null && state.Endpoint != Endpoint(request.SocketPath) && state.Endpoint != Endpoint(request.SocketPath + ".r")
            && (recovery is null || state.Endpoint != Endpoint(recovery.SocketPath))) throw new InvalidDataException("Unknown Windows handoff endpoint.");
        if (state.Status is "restarted" or "restored" && (state.ProcessIdentity is null || state.Endpoint is null || string.IsNullOrWhiteSpace(state.NativeEpoch)))
            throw new InvalidDataException("Successful Windows handoff state lacks exact native identity.");
        var previous = await WindowsVerifiedVersions.InspectExecutableAsync(root, intent.PreviousVersion.NativeExecutable, token);
        if (previous != intent.PreviousVersion) throw new InvalidDataException("The previous Windows version changed after handoff.");
        return new(intent, state, previous, recovery);
    }

    internal static string Root(string path, Guid operation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows update inspection requires Windows.");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || operation == Guid.Empty)
            throw new ArgumentException("Use a local absolute installation and exact operation UUID.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    internal static bool SamePath(string a, string? b) => b is not null && Path.IsPathFullyQualified(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    internal static string Endpoint(string socket) => NativeIpcEndpoint.FromSocketPath(socket);
    internal static void Ordinary(string path)
    { if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Windows handoff data cannot be redirected."); }
    internal static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        Ordinary(path); await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 65536) throw new InvalidDataException("Invalid Windows handoff input size.");
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        CheckDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing Windows handoff data.");
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Repeated Windows handoff property."); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var element in value.EnumerateArray()) CheckDuplicates(element);
    }
}
