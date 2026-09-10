namespace KiCad.Automation.Distribution;

internal static class LinuxPayloadFingerprint
{
    internal static Task<string> ComputeAsync(string root, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux payload modes require Linux.");
        return PosixPayloadFingerprint.ComputeAsync(root, token);
    }
}
