using System.ComponentModel;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>One service-owned registry. Intake sessions never launch or close editors.</summary>
public sealed class FileIntakeRegistry : IAsyncDisposable
{
    private sealed record Entry(string InstanceId, string RecoveryPath, string DesignPath, DesignFileIntakeSession Session);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool disposed;

    public string Start(string instanceId, string recoveryPath, string designPath)
    {
        if (!Guid.TryParseExact(instanceId, "D", out var id) || id == Guid.Empty
            || !Path.IsPathFullyQualified(recoveryPath) || !Path.IsPathFullyQualified(designPath))
            throw new AutomationException("invalid_intake_target", "Provide an explicit instance and absolute recovery and design paths.");
        recoveryPath = Path.GetFullPath(recoveryPath); designPath = Path.GetFullPath(designPath);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(recoveryPath, designPath, comparison))
            throw new AutomationException("invalid_intake_target", "Design and recovery records must be separate files.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.Values.Any(e => string.Equals(e.RecoveryPath, recoveryPath, comparison) || string.Equals(e.DesignPath, designPath, comparison)))
                throw new AutomationException("intake_ownership_conflict", "Stop the existing intake for this design or recovery record first.");
            string sessionId = Guid.NewGuid().ToString("D");
            entries.Add(sessionId, new(instanceId, recoveryPath, designPath,
                new(new DesignRecoveryStore(recoveryPath), id, designPath)));
            return sessionId;
        }
    }

    public DesignFileIntakeSession Get(string instanceId, string intakeId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(intakeId, out var entry) || entry.InstanceId != instanceId)
                throw new AutomationException("unknown_intake", "No intake session matches both explicit IDs.");
            return entry.Session;
        }
    }

    public IReadOnlyList<(string IntakeId, DesignFileIntakeStatus Status)> List(string instanceId)
    {
        if (!Guid.TryParseExact(instanceId, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_intake_target", "Provide an explicit instance ID.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return entries.Where(pair => pair.Value.InstanceId == instanceId)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value.Session.Inspect())).ToArray();
        }
    }

    public async Task<DesignFileIntakeStatus> StopAsync(string instanceId, string intakeId)
    {
        var session = Get(instanceId, intakeId); await session.DisposeAsync();
        lock (gate) entries.Remove(intakeId);
        return session.Inspect();
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] owned;
        lock (gate) { disposed = true; owned = entries.Values.ToArray(); }
        await Task.WhenAll(owned.Select(async e => await e.Session.DisposeAsync()));
        lock (gate) entries.Clear();
    }
}

[McpServerToolType]
public sealed class FileIntakeTools(FileIntakeRegistry registry)
{
    [McpServerTool(Name = "kicad_design_intake_list", ReadOnly = true),
     Description("List this service's current intake sessions for one explicit instance, including paused or stopped sessions awaiting removal. Use after a lost start reply to recover the intake ID. An empty list does not prove the editor is closed. Does not discover sessions from a previous service process or other services.")]
    public Task<CallToolResult> List(string instanceId, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = registry.List(instanceId);
        var data = JsonSerializer.SerializeToElement(new { instanceId,
            sessions = sessions.Select(s => new { intakeId = s.IntakeId, sequence = s.Status.Sequence,
                phase = s.Status.Phase.ToString(), errorCode = s.Status.ErrorCode, errorMessage = s.Status.ErrorMessage }),
            liveMutationAuthorized = false });
        return Task.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data
        });
    });

    [McpServerTool(Name = "kicad_design_intake_start"),
     Description("Start continuous local design-file to recovery-record intake for an explicit instance and absolute paths. Requires an existing matching recovery record and design parent directory. Captures future saved bytes, including invalid XML, and invalidates reviewed choices when bytes change. Never edits KiCad, design files or the baseline. Returns a process-local intake ID; this is not automatic bidirectional synchronization. Duplicate design/recovery paths in this service are rejected. Stop intake before replacing its watched directory; service shutdown awaits all owned intake tasks.")]
    public Task<CallToolResult> Start(string instanceId, string recoveryPath, string designPath, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string intakeId = registry.Start(instanceId, recoveryPath, designPath);
        return Task.FromResult(Result(instanceId, intakeId, registry.Get(instanceId, intakeId).Inspect()));
    });

    [McpServerTool(Name = "kicad_design_intake_wait", ReadOnly = true),
     Description("Inspect latest saved-file intake status or wait for a newer status sequence. Omit afterSequence for immediate inspection; supply the last sequence to wait. Returns latest state, not an event history. InvalidDesign keeps watching for corrected saves; Paused requires explicit resume after fixing persistence. Wait cancellation does not stop intake. Does not observe or change KiCad.")]
    public Task<CallToolResult> Wait(string instanceId, string intakeId, CancellationToken cancellationToken,
        ulong? afterSequence = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = registry.Get(instanceId, intakeId);
        return Result(instanceId, intakeId, afterSequence is { } cursor ? await session.WaitAsync(cursor, cancellationToken) : session.Inspect());
    });

    [McpServerTool(Name = "kicad_design_intake_resume"),
     Description("Resume exactly the currently paused file intake after repairing a missing file or persistence problem. Requires its latest status sequence; stale or non-paused requests fail. A stopped/replaced directory requires stopping and starting a new session. Returns current status; use wait for the retry result. Does not edit native designs.")]
    public Task<CallToolResult> Resume(string instanceId, string intakeId, ulong expectedSequence, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested(); var session = registry.Get(instanceId, intakeId);
        session.Resume(expectedSequence); return Task.FromResult(Result(instanceId, intakeId, session.Inspect()));
    });

    [McpServerTool(Name = "kicad_design_intake_stop"),
     Description("Stop the exact file intake session and await its task before releasing ownership. No further recovery writes occur after success. Does not close KiCad or discard any design/recovery files. Cancellation before dispatch stops nothing; once stopping starts cleanup completes.")]
    public Task<CallToolResult> Stop(string instanceId, string intakeId, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Result(instanceId, intakeId, await registry.StopAsync(instanceId, intakeId));
    });

    private static CallToolResult Result(string instanceId, string intakeId, DesignFileIntakeStatus status)
    {
        var data = JsonSerializer.SerializeToElement(new { instanceId, intakeId, sequence = status.Sequence,
            phase = status.Phase.ToString(), observation = status.Observation, errorCode = status.ErrorCode,
            errorMessage = status.ErrorMessage, liveMutationAuthorized = false });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            var data = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException a ? a.Code : "intake_unavailable", errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}
