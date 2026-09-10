namespace KiCad.Automation.Distribution;

public sealed record VerifiedMacActivation(UpdateActivation Activation, InstalledMacUpdate SelectedVersion);

public static partial class MacVerifiedInstallation
{
    public static async Task<InstalledMacUpdate> InspectActivationAsync(string installationRoot, string expectedTarget,
        string manifestSha256, CancellationToken token = default)
    {
        string root = RootPath(installationRoot);
        if (!Digest(manifestSha256)) throw new ArgumentException("Use a registered Mac manifest digest.");
        var policy = await Policy(root, token);
        using var ownership = await UpdateStoreLease.AcquireAsync(Path.Combine(root, "registration.lock"), token);
        if (InspectTarget(root) != expectedTarget) throw new InvalidDataException("Mac selection changed before activation preflight.");
        var baseline = await ReadVersion(CurrentVersion(root, expectedTarget), policy, token);
        var candidate = await ReadVersion(Path.Combine(root, "versions", manifestSha256), policy, token);
        RequireForward(baseline, candidate);
        await RequireAccepted(root, policy, baseline, candidate, token);
        return Describe(root, candidate, policy.Platform);
    }

    /// <summary>Commit only the selected version. Caller owns user action and editor close/restart.</summary>
    public static async Task<VerifiedMacActivation> ActivateAsync(string installationRoot, string expectedTarget,
        string manifestSha256, Guid operationId, CancellationToken token = default)
    {
        string root = RootPath(installationRoot);
        if (operationId == Guid.Empty || !Digest(manifestSha256)) throw new ArgumentException("Use an exact Mac candidate and non-empty operation ID.");
        var policy = await Policy(root, token);
        using var ownership = await UpdateStoreLease.AcquireAsync(Path.Combine(root, "registration.lock"), token);
        string manager = Path.Combine(root, "manager");
        string current = PosixUpdateActivation.InspectTarget(manager);
        string target = "selections/" + operationId.ToString("D");
        if (current != expectedTarget && current != target) throw new InvalidDataException("Mac selection changed before activation.");
        var baseline = await ReadVersion(CurrentVersion(root, expectedTarget), policy, token);
        var candidate = await ReadVersion(Path.Combine(root, "versions", manifestSha256), policy, token);
        using var checkpoint = await UpdateStoreLease.AcquireAsync(Path.Combine(root, "state/check.lock"), token);
        if (current != target)
        {
            RequireForward(baseline, candidate);
            await RequireAccepted(root, policy, baseline, candidate, token);
        }
        var selected = Describe(root, candidate, policy.Platform);
        var result = await PosixUpdateActivation.SwitchAsync(manager, expectedTarget, selected.VersionDirectory, operationId, token);
        return new(result, selected);
    }

    public static async Task<VerifiedMacActivation> RollbackAsync(string installationRoot, string expectedTarget,
        Guid updateOperationId, Guid rollbackOperationId, CancellationToken token = default)
    {
        string root = RootPath(installationRoot);
        if (updateOperationId == Guid.Empty || rollbackOperationId == Guid.Empty || updateOperationId == rollbackOperationId)
            throw new ArgumentException("Use distinct non-empty update and rollback IDs.");
        var policy = await Policy(root, token);
        using var ownership = await UpdateStoreLease.AcquireAsync(Path.Combine(root, "registration.lock"), token);
        string manager = Path.Combine(root, "manager");
        var original = await ReadJson<UpdateActivation>(Path.Combine(manager, "operations", updateOperationId.ToString("D") + ".json"), 16 * 1024, token);
        if (original.OperationId != updateOperationId.ToString("D") || original.Status != "prepared"
            || original.Target != "selections/" + updateOperationId.ToString("D") || original.Target != expectedTarget)
            throw new InvalidDataException("Rollback does not identify the selected Mac update.");
        string current = PosixUpdateActivation.InspectTarget(manager);
        if (current != expectedTarget && current != "selections/" + rollbackOperationId.ToString("D"))
            throw new InvalidDataException("A later Mac selection superseded this rollback.");
        var previous = await ReadVersion(CurrentVersion(root, original.PreviousTarget), policy, token);
        var selected = Describe(root, previous, policy.Platform);
        var result = await PosixUpdateActivation.SwitchAsync(manager, expectedTarget, selected.VersionDirectory, rollbackOperationId, token);
        // Accepted feed remains unchanged: rollback is not permission to accept an old network release.
        return new(result, selected);
    }

    private static void RequireForward(VerifiedUpdateManifest baseline, VerifiedUpdateManifest candidate)
    {
        if (candidate.Release.Sequence < baseline.Release.Sequence
            || (candidate.Release.Sequence == baseline.Release.Sequence && candidate.PayloadSha256 != baseline.PayloadSha256))
            throw new InvalidDataException("Mac activation cannot downgrade or replace an accepted release identity.");
    }
}
