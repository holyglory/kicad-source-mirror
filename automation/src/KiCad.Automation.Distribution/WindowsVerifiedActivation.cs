namespace KiCad.Automation.Distribution;

public static partial class WindowsVerifiedVersions
{
    public static async Task<VerifiedWindowsVersion> InspectActivationAsync(string root, string expectedSelectionId,
        string candidateDigest, CancellationToken token = default)
    {
        root = Root(root); RequireId(expectedSelectionId);
        if (!Digest(candidateDigest)) throw new ArgumentException("An exact candidate digest is required.");
        var policy = await Policy(root, token);
        using var registration = Lock(Path.Combine(root, "registration.lock"));
        var current = WindowsVersionSelection.Inspect(Path.Combine(root, "manager"));
        if (current.SelectionId != expectedSelectionId) throw new InvalidDataException("Windows selection changed before update preflight.");
        var baseline = await ReadVersion(root, DigestOf(current), policy, token);
        var candidate = await ReadVersion(root, candidateDigest, policy, token);
        if (candidate.Release.Sequence < baseline.Release.Sequence
            || (candidate.Release.Sequence == baseline.Release.Sequence && candidate.PayloadSha256 != baseline.PayloadSha256))
            throw new InvalidDataException("Windows update preflight cannot downgrade or replace an accepted release identity.");
        await RequireAccepted(root, policy, baseline, candidate, token);
        return Describe(root, candidate);
    }

    // These transitions authenticate both sides but never close an editor or
    // infer the user's Update action. The native lifecycle integration owns that.
    public static async Task<SelectedWindowsVersion> ActivateAsync(string root, string expectedSelectionId,
        string candidateDigest, Guid operationId, CancellationToken token = default)
    {
        root = Root(root); RequireId(expectedSelectionId);
        if (!Digest(candidateDigest) || operationId == Guid.Empty) throw new ArgumentException("An exact candidate and update operation are required.");
        var policy = await Policy(root, token);
        using var registration = Lock(Path.Combine(root, "registration.lock"));
        string manager = Path.Combine(root, "manager");
        var current = WindowsVersionSelection.Inspect(manager);
        WindowsSelectedVersion previous = current;
        if (current.SelectionId != expectedSelectionId)
        {
            if (current.SelectionId != operationId.ToString("D")) throw new InvalidDataException("Windows selection changed before activation.");
            var intent = WindowsVersionSelection.InspectIntent(manager, operationId);
            if (intent.Previous.SelectionId != expectedSelectionId || DigestOf(intent.Next) != candidateDigest || intent.Next != current)
                throw new InvalidDataException("Windows activation retry has different arguments.");
            previous = intent.Previous;
        }
        var baseline = await ReadVersion(root, DigestOf(previous), policy, token);
        var candidate = await ReadVersion(root, candidateDigest, policy, token);
        using var checkpoint = Lock(Path.Combine(root, "state/check.lock"));
        if (current.SelectionId != operationId.ToString("D"))
        {
            if (candidate.Release.Sequence < baseline.Release.Sequence
                || (candidate.Release.Sequence == baseline.Release.Sequence && candidate.PayloadSha256 != baseline.PayloadSha256))
                throw new InvalidDataException("Windows activation cannot downgrade or replace an accepted release identity.");
            await RequireAccepted(root, policy, baseline, candidate, token);
        }
        var next = await WindowsVersionSelection.SwitchAsync(manager, previous, candidateDigest, operationId, token);
        return new(Describe(root, candidate), next.SelectionId);
    }

    public static async Task<SelectedWindowsVersion> RollbackAsync(string root, string expectedSelectionId,
        Guid originalOperationId, Guid rollbackOperationId, CancellationToken token = default)
    {
        root = Root(root); RequireId(expectedSelectionId);
        if (originalOperationId == Guid.Empty || rollbackOperationId == Guid.Empty || originalOperationId == rollbackOperationId)
            throw new ArgumentException("Use distinct update and rollback operations.");
        var policy = await Policy(root, token);
        using var registration = Lock(Path.Combine(root, "registration.lock"));
        string manager = Path.Combine(root, "manager");
        var original = WindowsVersionSelection.InspectIntent(manager, originalOperationId);
        if (original.Next.SelectionId != expectedSelectionId)
            throw new InvalidDataException("Rollback does not identify the selected Windows update.");
        var current = WindowsVersionSelection.Inspect(manager);
        if (current != original.Next && current.SelectionId != rollbackOperationId.ToString("D"))
            throw new InvalidDataException("A later Windows selection superseded this rollback.");
        // Authenticate the predecessor rather than trusting a local path in a receipt.
        var previous = await ReadVersion(root, DigestOf(original.Previous), policy, token);
        // A broken newly selected payload must not prevent recovery to its
        // verified predecessor. The exact selected operation still gates rollback.
        var selected = await WindowsVersionSelection.SwitchAsync(manager, original.Next, previous.PayloadSha256, rollbackOperationId, token);
        return new(Describe(root, previous), selected.SelectionId); // Accepted network checkpoint is not lowered.
    }
}
