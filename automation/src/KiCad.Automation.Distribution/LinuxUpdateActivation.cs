namespace KiCad.Automation.Distribution;

public sealed record UpdateActivation(string OperationId, string PreviousTarget, string Target, string VersionTarget, string Status);

/// <summary>Preserves the Linux-only public contract while sharing the Unix link transaction.</summary>
public static class LinuxUpdateActivation
{
    public static Task InitializeAsync(string installationRoot, string firstVersion, CancellationToken token = default)
    {
        RequireLinux();
        return PosixUpdateActivation.InitializeAsync(installationRoot, firstVersion, token);
    }

    public static Task<UpdateActivation> SwitchAsync(string installationRoot, string expectedTarget,
        string nextVersion, Guid operationId, CancellationToken token = default) =>
        SwitchAsync(installationRoot, expectedTarget, nextVersion, operationId, null, token);

    internal static Task<UpdateActivation> SwitchAsync(string installationRoot, string expectedTarget,
        string nextVersion, Guid operationId, Action? beforeSwitch, CancellationToken token)
    {
        RequireLinux();
        return PosixUpdateActivation.SwitchAsync(installationRoot, expectedTarget, nextVersion, operationId, beforeSwitch, token);
    }

    public static string InspectTarget(string installationRoot)
    {
        RequireLinux();
        return PosixUpdateActivation.InspectTarget(installationRoot);
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This installation switch is Linux-specific.");
    }
}
