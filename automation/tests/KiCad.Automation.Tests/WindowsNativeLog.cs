namespace KiCad.Automation.Tests;

internal static class WindowsNativeLog
{
    public static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(input);
        return await reader.ReadToEndAsync(token);
    }
}
