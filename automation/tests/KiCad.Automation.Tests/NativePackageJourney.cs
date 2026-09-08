using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Native;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    [TestMethod]
    [TestCategory("NativePackage")]
    public async Task InstalledNativeAndSelfContainedMcpCreateRenderAndSave()
    {
        Assert.IsTrue(OperatingSystem.IsLinux());
        string root = FindRoot();
        string pointer = Path.Combine(root, "automation/artifacts/distribution/current-staging.json");
        var stage = JsonSerializer.Deserialize<LinuxStageResult>(await File.ReadAllTextAsync(pointer))!;
        Assert.AreEqual("staged", stage.Status);
        Assert.IsFalse(stage.QualifyingDelivery);
        string installRoot = Path.Combine(stage.Directory, "root");
        string prefix = Path.Combine(installRoot, "usr/local");
        LinuxStaging.VerifyTree(prefix);
        // Verify the actual installed bytes, including the self-contained runtime,
        // before invoking any of them. This is not publisher authentication.
        foreach (var file in stage.Files)
        {
            string path = Path.Combine(installRoot, file.Path);
            Assert.AreEqual(file.Bytes, new FileInfo(path).Length);
            Assert.AreEqual(file.Sha256, Evidence.Hash(path), file.Path);
        }
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(root, "automation/artifacts"));
        await File.WriteAllTextAsync(Path.Combine(evidence, "package-receipt.sha256"), Evidence.Hash(pointer));
        await VerifyInstalledNative(prefix, evidence);
    }

    private static async Task VerifyInstalledNative(string prefix, string evidence,
        string? nativeLauncher = null, string? mcpLauncher = null)
    {
        string temporary = Directory.CreateTempSubdirectory("kicad-package-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var processes = new List<Process>();
        var captures = new List<Task>();
        try
        {
            var displayStart = new ProcessStartInfo("Xvfb")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
                displayStart.ArgumentList.Add(arg);
            Process display = Process.Start(displayStart)!;
            processes.Add(display);
            captures.Add(Capture(display.StandardError, Path.Combine(evidence, "package-xvfb.stderr.log")));
            string? displayNumber = await display.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.IsTrue(int.TryParse(displayNumber, out _));
            string instanceId = Guid.NewGuid().ToString("D");
            string rootId = Guid.NewGuid().ToString("D");
            string project = Path.Combine(temporary, "fixture.kicad_pro");
            string schematic = Path.Combine(temporary, "fixture.kicad_sch");
            await File.WriteAllTextAsync(project, JsonSerializer.Serialize(new
            {
                meta = new { version = 3 },
                schematic = new { top_level_sheets = new[]
                { new { uuid = rootId, name = "fixture", filename = "fixture.kicad_sch" } } }
            }), deadline.Token);
            string socket = Path.Combine(temporary, "api.sock");
            var start = new ProcessStartInfo(nativeLauncher ?? Path.Combine(prefix, "bin/kicad"))
            {
                WorkingDirectory = temporary, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment["DISPLAY"] = ":" + displayNumber;
            start.Environment.Remove("KICAD_RUN_FROM_BUILD_DIR");
            start.Environment["KICAD_STOCK_DATA_HOME"] = Path.Combine(prefix, "share/kicad");
            // This is a normal prefix install, not an AppImage directory.
            start.Environment.Remove("APPDIR");
            start.Environment["LD_LIBRARY_PATH"] = Path.Combine(prefix, "lib");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(temporary, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(temporary, "cache");
            foreach (string arg in new[] { "--new", "--automation", instanceId, "--api-socket", socket,
                "--automation-log", Path.Combine(evidence, "installed-native.log"), "--software-rendering", project })
                start.ArgumentList.Add(arg);
            Process native = Process.Start(start)!;
            processes.Add(native);
            captures.Add(Capture(native.StandardOutput, Path.Combine(evidence, "installed-native.stdout.log")));
            captures.Add(Capture(native.StandardError, Path.Combine(evidence, "installed-native.stderr.log")));
            var registry = new InstanceRegistry(new NngTransport(), Path.Combine(temporary, "registry"));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.IsFalse(native.HasExited, "Installed manager exited; inspect retained native diagnostics.");
                try { await registry.AttachAsync("ipc://" + socket, instanceId, deadline.Token); break; }
                catch (NngException) { await Task.Delay(100, deadline.Token); }
                catch (NativeApiException error) when (error.Status is 4 or 7)
                { await Task.Delay(100, deadline.Token); }
            }
            var client = new NativeClient(new NngTransport(), "ipc://" + socket);
            await VerifyEmptyRootCreation(client, schematic, Path.Combine(temporary, "wrong.kicad_sch"),
                evidence, instanceId, rootId, deadline.Token,
                mcpLauncher ?? Path.Combine(prefix, "lib/kicad-automation/kicad-mcp"));
            Assert.IsFalse(native.HasExited, "MCP exit must not close the installed editor.");
        }
        finally
        {
            foreach (Process process in processes.AsEnumerable().Reverse())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await Task.WhenAll(captures);
            Directory.Delete(temporary, recursive: true);
        }
    }
}
