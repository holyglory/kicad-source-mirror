namespace KiCad.Automation.Mcp;

// Only background candidate registration may refresh its internal selection
// snapshot. User-authorized activation and native edits retain exact stale checks.
internal static class UpdateRegistrationRetry
{
    public static async Task<T> AgainstCurrentSelection<T>(Func<string> inspect, Func<string, Task<T>> register)
    {
        for (int attempt = 0; ; attempt++)
        {
            string expected = inspect();
            try { return await register(expected); }
            catch (InvalidDataException) when (attempt < 2 && inspect() != expected) { }
        }
    }
}
