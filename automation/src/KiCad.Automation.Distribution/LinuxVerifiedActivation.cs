namespace KiCad.Automation.Distribution;

public sealed record VerifiedLinuxActivation(UpdateActivation Activation, InstalledLinuxUpdate SelectedVersion);

public static partial class LinuxVerifiedInstallation
{
    /// <summary>Activates only a registered, unchanged candidate. This is the
    /// selection commit, not editor shutdown or permission to restart them.</summary>
    public static async Task<VerifiedLinuxActivation> ActivateAsync(string root, string expectedTarget,
        string manifestSha256, Guid operationId, CancellationToken token = default)
    {
        root = ActivationRoot(root, operationId);
        if (!DigestName(manifestSha256)) throw new ArgumentException("Use an exact registered manifest digest.", nameof(manifestSha256));
        token.ThrowIfCancellationRequested();
        var policy = await ActivationPolicyAsync(root, token);
        byte[] key = Convert.FromBase64String(policy.PublisherKeySpki);
        using var ownership = new FileStream(Path.Combine(root, "registration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string manager = Path.Combine(root, "manager");
        string current = LinuxUpdateActivation.InspectTarget(manager);
        string operationTarget = "selections/" + operationId.ToString("D");
        if (current != expectedTarget && current != operationTarget)
            throw new InvalidDataException("The active installation changed before verified activation.");
        var baseline = await ReadVersionAsync(CurrentVersion(root, expectedTarget), key, policy, token);
        var candidate = await ReadVersionAsync(Path.Combine(root, "versions", manifestSha256), key, policy, token);
        using var checkpoint = new FileStream(Path.Combine(root, "state", "check.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (current != operationTarget)
        {
            if (candidate.Release.Sequence < baseline.Release.Sequence
                || candidate.Release.Sequence == baseline.Release.Sequence && candidate.PayloadSha256 != baseline.PayloadSha256)
                throw new InvalidDataException("A normal activation cannot downgrade or replace an accepted release identity.");
            await RequireAcceptedSequenceAsync(root, key, policy.Channel, baseline, candidate, token);
        }
        var selected = DescribeVersion(root, candidate);
        var result = await LinuxUpdateActivation.SwitchAsync(manager, expectedTarget, selected.VersionDirectory, operationId, token);
        return new(result, selected);
    }

    /// <summary>Restores only the retained predecessor of the specified update.
    /// It does not reclassify an older feed as acceptable, or reset its cursor.</summary>
    public static async Task<VerifiedLinuxActivation> RollbackAsync(string root, string expectedTarget,
        Guid updateOperationId, Guid rollbackOperationId, CancellationToken token = default)
    {
        root = ActivationRoot(root, rollbackOperationId);
        if (updateOperationId == Guid.Empty || updateOperationId == rollbackOperationId)
            throw new ArgumentException("Use distinct non-empty update and rollback operation IDs.");
        token.ThrowIfCancellationRequested();
        var policy = await ActivationPolicyAsync(root, token);
        byte[] key = Convert.FromBase64String(policy.PublisherKeySpki);
        using var ownership = new FileStream(Path.Combine(root, "registration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string manager = Path.Combine(root, "manager");
        var original = await ReadJsonAsync<UpdateActivation>(Path.Combine(manager, "operations",
            updateOperationId.ToString("D") + ".json"), 16 * 1024, token);
        if (original.OperationId != updateOperationId.ToString("D") || original.Status != "prepared"
            || original.Target != "selections/" + updateOperationId.ToString("D") || expectedTarget != original.Target)
            throw new InvalidDataException("Rollback does not identify the requested update's selected version.");
        string current = LinuxUpdateActivation.InspectTarget(manager);
        if (current != expectedTarget && current != "selections/" + rollbackOperationId.ToString("D"))
            throw new InvalidDataException("A later selection superseded this rollback request.");
        var previous = await ReadVersionAsync(CurrentVersion(root, original.PreviousTarget), key, policy, token);
        var selected = DescribeVersion(root, previous);
        // Keep accepted-envelope.json unchanged. Only this exact known-good
        // predecessor is selected; arbitrary downgrade requests stay rejected.
        var result = await LinuxUpdateActivation.SwitchAsync(manager, expectedTarget, selected.VersionDirectory, rollbackOperationId, token);
        return new(result, selected);
    }

    private static string ActivationRoot(string root, Guid operationId)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Verified activation requires Linux.");
        if (!Path.IsPathFullyQualified(root) || operationId == Guid.Empty)
            throw new ArgumentException("Use an absolute installation root and a non-empty operation ID.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static async Task<LinuxInstallationPolicy> ActivationPolicyAsync(string root, CancellationToken token)
    {
        var policy = await ReadJsonAsync<LinuxInstallationPolicy>(Path.Combine(root, "publisher.json"), 16 * 1024, token);
        if (policy.SchemaVersion != 1) throw new InvalidDataException("Unsupported installation publisher policy.");
        using var origin = new UpdateDownloader(new Uri(policy.Origin, UriKind.Absolute));
        return policy;
    }
}
