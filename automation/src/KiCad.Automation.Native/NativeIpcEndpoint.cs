namespace KiCad.Automation.Native;

/// <summary>Same-host NNG addresses shared by native requests and notifications.</summary>
public static class NativeIpcEndpoint
{
    public static string FromSocketPath(string socketPath)
    {
        if (!Path.IsPathFullyQualified(socketPath))
            throw new ArgumentException("An absolute socket path is required.", nameof(socketPath));
        string endpoint = "ipc://" + socketPath;
        NngTransport.ValidateEndpoint(endpoint);
        return endpoint;
    }

    internal static string RuntimeDirectory(string instanceId)
    {
        if (!Guid.TryParseExact(instanceId, "D", out _))
            throw new ArgumentException("An instance UUID is required.", nameof(instanceId));
        // Keep Unix socket names short even when the caller's TMPDIR is long.
        // Windows needs a fully qualified, writable user-local directory.
        return Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
            "kicad-automation", instanceId);
    }
}
