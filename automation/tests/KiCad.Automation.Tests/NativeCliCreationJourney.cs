using System.Diagnostics;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyCliCreationRejection(string manager, string display, string temporary,
        string evidence, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(temporary, "cli-creation")).FullName;
        string socket = Path.Combine(directory, "api.sock");
        string schematic = Path.Combine(directory, "fixture.kicad_sch");
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(manager)!, "kicad-cli"))
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["DISPLAY"] = display;
        start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
        foreach (string arg in new[] { "api-server", "--socket", socket }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = Capture(process.StandardOutput, Path.Combine(evidence, "cli-creation.stdout.log"));
        var stderr = Capture(process.StandardError, Path.Combine(evidence, "cli-creation.stderr.log"));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var client = new NativeClient(new NngTransport(), "ipc://" + socket);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(process.HasExited, "The isolated CLI peer exited before serving requests.");
                try { await client.GetVersionAsync(deadline.Token); break; }
                catch (NngException) { }
                catch (NativeApiException error) when (error.Status is 4 or 7) { }
                await Task.Delay(100, deadline.Token);
            }
            var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.CreateRootSchematicAsync(schematic, deadline.Token));
            Assert.AreEqual(8, rejected.Status, "Headless CLI must reject, not ignore, the graphical creation flag.");
            Assert.IsFalse(File.Exists(schematic));
            Assert.IsNotNull((await client.GetVersionAsync(deadline.Token)).Version,
                "An unsupported request must leave the peer responsive.");
        }
        finally
        {
            // This process is created only for the isolated governed fixture.
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }
}
