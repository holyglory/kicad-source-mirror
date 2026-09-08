using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyRootCreationRejections(string executable, string display, string temporary,
        string evidence, CancellationToken token)
    {
        foreach (string scenario in new[] { "multiple-roots", "different-filename" })
        {
            string directory = Directory.CreateDirectory(Path.Combine(temporary, scenario)).FullName;
            string project = Path.Combine(directory, "fixture.kicad_pro");
            string schematic = Path.ChangeExtension(project, ".kicad_sch");
            string id = Guid.NewGuid().ToString("D");
            string socket = Path.Combine(directory, "api.sock");
            var roots = (scenario == "multiple-roots" ? new[] { "fixture.kicad_sch", "extra.kicad_sch" }
                : new[] { "different.kicad_sch" }).Select(filename => new { uuid = Guid.NewGuid().ToString("D"), name = filename, filename });
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
                { meta = new { version = 3 }, schematic = new { top_level_sheets = roots } }), token);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment["DISPLAY"] = display;
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
            foreach (string arg in new[] { "--new", "--automation", id, "--api-socket", socket,
                "--automation-log", Path.Combine(evidence, scenario + ".native.log"), "--software-rendering", project })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = Capture(process.StandardOutput, Path.Combine(evidence, scenario + ".stdout.log"));
            var stderr = Capture(process.StandardError, Path.Combine(evidence, scenario + ".stderr.log"));
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var client = new NativeClient(new NngTransport(), "ipc://" + socket);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Assert.IsFalse(process.HasExited, "The isolated rejection peer exited during startup.");
                    try { await client.HandshakeAsync(deadline.Token); break; }
                    catch (NngException) { }
                    catch (NativeApiException error) when (error.Status is 4 or 7) { }
                    await Task.Delay(100, deadline.Token);
                }
                byte[] before = await File.ReadAllBytesAsync(project, deadline.Token);
                var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.CreateRootSchematicAsync(schematic, deadline.Token));
                Assert.AreEqual(3, rejected.Status, scenario);
                Assert.IsFalse(File.Exists(schematic));
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(project, deadline.Token),
                    "Rejected creation must not rewrite project root declarations.");
                Assert.AreEqual(id, (await client.HandshakeAsync(deadline.Token)).InstanceId);
            }
            catch
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, scenario + "-failed.png"), CancellationToken.None);
                throw;
            }
            finally
            {
                // Dispose only the native process created for this isolated case.
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
            }
        }
    }
}
