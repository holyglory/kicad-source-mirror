using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using KiCad.Automation.Mcp;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsLauncherTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SelectedLaunchPreservesArgumentBoundariesAndUsesItsVersion()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kwlaunch"));
        var version = new VerifiedWindowsVersion(root, Path.Combine(root, "versions", new string('1', 64), "payload"), new string('1', 64), new string('2', 40));
        string[] arguments = ["", "space here", "quote\"here", "trailing\\", "ユニコード"];
        var launch = WindowsLaunchCommand.StartInfo(version, "mcp", arguments);
        CollectionAssert.AreEqual(arguments, launch.ArgumentList.ToArray());
        Assert.AreEqual(version.McpExecutable, launch.FileName); Assert.IsFalse(launch.UseShellExecute);
        Assert.AreEqual(Environment.CurrentDirectory, launch.WorkingDirectory);
        Assert.AreEqual(version.UpdateConfiguration, launch.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"]);
        Assert.IsFalse(launch.Environment.ContainsKey("KICAD_AUTOMATION_NNG_LIBRARY"));
        Assert.AreEqual(version.NativeExecutable, WindowsLaunchCommand.StartInfo(version, "native", []).FileName);
        Assert.ThrowsExactly<ArgumentException>(() => WindowsLaunchCommand.StartInfo(version, "other", []));
    }

    [TestMethod]
    public async Task InvalidLaunchCannotCreateAnInstallationOrStartMcp()
    {
        using var error = new StringWriter();
        Assert.AreEqual(1, await WindowsLaunchCommand.RunAsync(["--launch-installed"], error, default));
        using var result = JsonDocument.Parse(error.ToString());
        Assert.AreEqual("failed", result.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(result.RootElement.GetProperty("nativeEditorReady").GetBoolean());
    }

    [TestMethod]
    public async Task NativeRootLaunchersPreserveUnicodeStdioAndRejectChangedBootstrap()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows launchers."); return; }
        string scratch = Directory.CreateTempSubdirectory("kwlauncher-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!, "windows-launcher")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            string build = await Build(scratch, evidence, deadline.Token);
            string root = Directory.CreateDirectory(Path.Combine(scratch, "installed space ユニコード")).FullName;
            string digest = new('1', 64), bin = Directory.CreateDirectory(Path.Combine(root, "versions", digest, "payload/bin")).FullName;
            File.Copy(Path.Combine(build, "launcher-probe.exe"), Path.Combine(bin, "kicad-mcp.exe"));
            File.Copy(Path.Combine(build, "kicad-automation-mcp-launcher.exe"), Path.Combine(root, "kicad-mcp.exe"));
            File.Copy(Path.Combine(build, "kicad-automation-launcher.exe"), Path.Combine(root, "kicad.exe"));
            string hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(bin, "kicad-mcp.exe"), deadline.Token)));
            string metadata = Path.Combine(root, "launcher-bootstrap.json");
            string record = JsonSerializer.Serialize(new { schemaVersion = 1, versionDigest = digest, helperSha256 = hash });
            await File.WriteAllTextAsync(metadata, record, deadline.Token);
            string[] arguments = ["", "sp ace", "embedded\"quote", "trailing\\", "slash\\\"quote", "ユニコード", "%PATH%", "&no-shell"];
            var result = await Invoke(Path.Combine(root, "kicad-mcp.exe"), arguments, root, deadline.Token);
            Assert.AreEqual(17, result.ExitCode, result.Error);
            File.Copy(Path.Combine(root, "bootstrap-probe.json"), Path.Combine(evidence, "console.json"));
            Assert.AreEqual((await File.ReadAllTextAsync(Path.Combine(evidence, "console.json"), deadline.Token)).Trim(), result.Output.Trim(),
                "The captured UTF-8 stream must match the native probe's UTF-8 argument record.");
            using (var json = JsonDocument.Parse(result.Output))
            {
                string[] passed = json.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToArray();
                CollectionAssert.AreEqual(new[] { "--launch-installed", "--installation", root, "--target", "mcp", "--" }.Concat(arguments).ToArray(), passed);
                Assert.AreEqual("input over inherited stdin\n", json.RootElement.GetProperty("input").GetString());
            }
            File.Delete(Path.Combine(root, "bootstrap-probe.json"));
            result = await Invoke(Path.Combine(root, "kicad.exe"), ["project with spaces.kicad_pro"], root, deadline.Token);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "bootstrap-probe.json"), deadline.Token)))
                Assert.AreEqual("native", json.RootElement.GetProperty("arguments")[4].GetString());
            File.Copy(Path.Combine(root, "bootstrap-probe.json"), Path.Combine(evidence, "gui.json"));
            File.Delete(Path.Combine(root, "bootstrap-probe.json"));
            foreach (string invalid in new[] { "{}", record.Replace(hash, new string('0', 64), StringComparison.Ordinal),
                record.Replace(digest, "../escape", StringComparison.Ordinal), record[..^1] + ",\"schemaVersion\":1}" })
            {
                await File.WriteAllTextAsync(metadata, invalid, deadline.Token);
                result = await Invoke(Path.Combine(root, "kicad-mcp.exe"), [], root, deadline.Token);
                Assert.AreEqual(1, result.ExitCode); Assert.IsFalse(File.Exists(Path.Combine(root, "bootstrap-probe.json")));
                StringAssert.Contains(result.Error, "KiCad launch failed");
            }
            await File.WriteAllTextAsync(metadata, record, deadline.Token);
            Assert.AreEqual(17, (await Invoke(Path.Combine(root, "kicad-mcp.exe"), ["recovered"], root, deadline.Token)).ExitCode);
            string caller = Directory.CreateDirectory(Path.Combine(scratch, "project working directory")).FullName;
            Assert.AreEqual(17, (await Invoke(Path.Combine(root, "kicad-mcp.exe"), ["relative.kicad_pro"], caller, deadline.Token)).ExitCode);
            Assert.IsTrue(File.Exists(Path.Combine(caller, "bootstrap-probe.json")), "A launcher must preserve the caller's working directory for relative project paths.");
            await File.WriteAllTextAsync(metadata, "{}", deadline.Token);
            using (var failedGui = Process.Start(new ProcessStartInfo(Path.Combine(root, "kicad.exe")) { UseShellExecute = false, WorkingDirectory = root })!)
            {
                try
                {
                    nint dialog = await WindowsNativeUi.WaitForWindow(failedGui, "KiCad launch failed", deadline.Token);
                    WindowsNativeUi.Capture(failedGui, dialog, Path.Combine(evidence, "launch-error.png"));
                    WindowsNativeUi.Close(failedGui, dialog);
                    await failedGui.WaitForExitAsync(deadline.Token); Assert.AreEqual(1, failedGui.ExitCode);
                }
                finally { if (!failedGui.HasExited) failedGui.Kill(true); await failedGui.WaitForExitAsync(); }
            }
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
        }
        finally { await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(scratch); }
    }

    internal static async Task<string> Build(string scratch, string evidence, CancellationToken token)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/native/windows-launcher/CMakeLists.txt"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string build = Path.Combine(scratch, "build");
        foreach (var (name, arguments) in new[]
        {
            ("configure", new[] { "-S", Path.Combine(repository.FullName, "automation/native/windows-launcher"), "-B", build,
                "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DKICAD_BUILD_LAUNCHER_FIXTURE=ON" }),
            ("build", new[] { "--build", build })
        })
        {
            var result = await Invoke("cmake", arguments, scratch, token, input: null);
            await File.WriteAllTextAsync(Path.Combine(evidence, name + ".stdout.log"), result.Output);
            await File.WriteAllTextAsync(Path.Combine(evidence, name + ".stderr.log"), result.Error);
            Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
        }
        return build;
    }

    internal static async Task<(int ExitCode, string Output, string Error)> Invoke(string executable, string[] args, string directory,
        CancellationToken token, string? input = "input over inherited stdin\n")
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null)
                try { await process.StandardInput.WriteAsync(input); }
                catch (IOException) { } // Early rejection may close stdin; exit and stderr remain authoritative.
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr);
        }
    }
}
