using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>Combines private handoff inspection with registry replacement.
/// No operation here closes, launches or signals an editor.</summary>
internal static class InstanceUpdateReconnection
{
    public static async Task<InstanceReplacementResult> ReconnectAsync(InstanceRegistry registry, string instanceId,
        string installationRoot, Guid operation, string expectedOldEpoch, CancellationToken token)
    {
        if (!Guid.TryParseExact(instanceId, "D", out var id) || id == Guid.Empty || operation == Guid.Empty
            || string.IsNullOrWhiteSpace(expectedOldEpoch))
            throw new AutomationException("invalid_replacement", "An explicit instance, operation and original process epoch are required.");
        UpdateReplacementProof? proof;
        string status;
        if (OperatingSystem.IsWindows())
        {
            var inspected = await WindowsUpdateInspection.InspectAsync(installationRoot, operation, token);
            status = inspected.Status; proof = inspected.Reconnection;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var inspected = await MacUpdateInspection.InspectAsync(installationRoot, operation, token);
            status = inspected.Status; proof = inspected.Reconnection;
        }
        else if (OperatingSystem.IsLinux())
        {
            var inspected = await LinuxUpdateInspection.InspectAsync(installationRoot, operation, token);
            status = inspected.Status; proof = inspected.Reconnection;
        }
        else throw new PlatformNotSupportedException("Native update reconnection is not supported on this platform.");
        if (status != "replacement_running" || proof is null)
            throw new AutomationException("replacement_unverified", "No live replacement with verified original-session evidence is available: " + status);
        return await AdoptInspectedAsync(registry, instanceId, operation, expectedOldEpoch, proof, token);
    }

    // Testable boundary; production obtains this object only from native inspection.
    internal static async Task<InstanceReplacementResult> AdoptInspectedAsync(InstanceRegistry registry, string instanceId,
        Guid operation, string expectedOldEpoch, UpdateReplacementProof proof, CancellationToken token)
    {
        if (operation == Guid.Empty || operation != proof.OperationId || instanceId != proof.InstanceId.ToString("D")
            || string.IsNullOrWhiteSpace(expectedOldEpoch) || expectedOldEpoch != proof.Origin.Epoch)
            throw new AutomationException("replacement_mismatch", "The handoff does not match the requested instance, operation and original epoch.");
        var previous = await registry.PreviousForVerifiedReplacementAsync(instanceId, operation, expectedOldEpoch, token);
        if (previous.Endpoint != proof.Origin.Endpoint || previous.ProjectPath != proof.ProjectPath
            || (previous.ProcessId is not null && previous.ProcessId != proof.OriginalProcessId))
            throw new AutomationException("replacement_mismatch", "The handoff does not match the original saved connection.");
        return await registry.AdoptVerifiedReplacementAsync(previous, proof.Endpoint, proof.Epoch,
            proof.ReplacementProcessId, operation, token);
    }
}
