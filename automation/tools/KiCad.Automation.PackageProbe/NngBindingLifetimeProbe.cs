using System.Text.Json;
using System.Runtime.InteropServices;
using KiCad.Automation.Native;

internal static class NngBindingLifetimeProbe
{
    internal static async Task<int> RunAsync(string library, bool reopenPath = false)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The removed-path fixture requires Unix symbolic links.");
        if (!Path.IsPathFullyQualified(library) || !File.Exists(library))
            throw new ArgumentException("Select the exact existing native NNG library for this isolated probe.");
        string root = Directory.CreateTempSubdirectory("kicad-nng-lifetime-").FullName;
        try
        {
            string alias = Path.Combine(root, OperatingSystem.IsMacOS() ? "selected.dylib" : "selected.so");
            File.CreateSymbolicLink(alias, library);
            if (reopenPath)
            {
                // Negative control: reproduce the former resolver in this
                // isolated test process, never in a shipped MCP process.
                Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", null);
                NativeLibrary.SetDllImportResolver(typeof(NngTransport).Assembly,
                    (name, _, _) => name == "nng" ? NativeLibrary.Load(alias) : IntPtr.Zero);
            }
            else Environment.SetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY", alias);
            string version = NngTransport.ProbeLibrary();
            File.Delete(alias); // Only this process's newly created alias, never the supplied library.
            bool timedOut = false;
            try
            {
                // ProbeLibrary has not used dial/send/strerror. Their lazy
                // bindings must reuse its native handle after alias removal.
                _ = await new NngTransport().ExchangeAsync("ipc:///tmp/nng-" + Guid.NewGuid().ToString("N") + ".sock",
                    [1, 2, 3], TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }
            catch (NngException error) when (error.ErrorCode == 5) { timedOut = true; }
            catch (DllNotFoundException) when (reopenPath)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, status = "expected_missing_library" }));
                return 2;
            }
            if (!timedOut) throw new InvalidDataException("The isolated absent peer did not reach the actual native timeout.");
            if (NngTransport.ProbeLibrary() != version) throw new InvalidDataException("The selected native NNG version changed.");
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, status = "passed", version,
                selectedPathRemoved = true, lateNativeBindingsVerified = true, originalLibraryPreserved = File.Exists(library) }));
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
