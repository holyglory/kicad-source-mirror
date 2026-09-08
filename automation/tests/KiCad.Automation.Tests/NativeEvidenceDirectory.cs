namespace KiCad.Automation.Tests;

internal static class NativeEvidenceDirectory
{
    // Coordinator serializes runs in this worktree. Preserve the previous
    // output, but retain only this run's tree rather than all past journeys.
    internal static string Begin(string artifacts)
    {
        string current = Path.Combine(artifacts, "native-session-current");
        if (Directory.Exists(current))
        {
            string history = Path.Combine(artifacts, "native-session-history");
            Directory.CreateDirectory(history);
            Directory.Move(current, Path.Combine(history, Guid.NewGuid().ToString("N")));
        }
        Directory.CreateDirectory(current);
        return current;
    }
}
