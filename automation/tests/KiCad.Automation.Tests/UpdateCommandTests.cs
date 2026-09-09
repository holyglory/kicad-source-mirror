using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateCommandTests
{
    [TestMethod]
    public void KernelStartCounterHandlesParenthesizedNamesAndRejectsMalformedValues()
    {
        string[] fields = ["S", .. Enumerable.Repeat("0", 18), "123456", "0"];
        Assert.AreEqual(123456UL, LinuxProcessIdentity.ParseStartTicks("17 (fixture (with) spaces) " + string.Join(' ', fields)));
        foreach (string invalid in new[] { "missing", "17 () S 0", "17 (x) " + string.Join(' ', fields.Select((value, i) => i == 19 ? "-1" : value)) })
            Assert.ThrowsExactly<InvalidDataException>(() => LinuxProcessIdentity.ParseStartTicks(invalid));
        if (OperatingSystem.IsLinux())
        {
            var first = LinuxProcessIdentity.Read(Environment.ProcessId);
            Assert.AreEqual(first, LinuxProcessIdentity.Read(Environment.ProcessId));
            Assert.AreNotEqual(Guid.Empty, first.BootId);
        }
    }

    [TestMethod]
    public async Task InvalidInvocationAndCancelledReadReturnExplicitNonReadyResults()
    {
        string root = Directory.CreateTempSubdirectory("kicad-update-command-").FullName;
        try
        {
            string configuration = Path.Combine(root, "configuration.json");
            foreach (string json in new[] { "broken", "{}", "{\"schemaVersion\":1,\"schemaVersion\":2}",
                "{\"schemaVersion\":1,\"unknown\":true}" })
            {
                await File.WriteAllTextAsync(configuration, json);
                using var output = new StringWriter();
                Assert.AreEqual(1, await UpdatePreparationCommand.RunAsync(
                    ["--prepare-update", "--configuration", configuration], output, CancellationToken.None));
                using var result = JsonDocument.Parse(output.ToString());
                Assert.AreEqual("failed", result.RootElement.GetProperty("status").GetString());
                Assert.IsFalse(result.RootElement.GetProperty("installationReady").GetBoolean());
            }
            using var invalid = new StringWriter();
            Assert.AreEqual(1, await UpdatePreparationCommand.RunAsync(["--prepare-update"], invalid, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var cancelled = new StringWriter();
            Assert.AreEqual(2, await UpdatePreparationCommand.RunAsync(
                ["--prepare-update", "--configuration", configuration], cancelled, cancellation.Token));
            using var message = JsonDocument.Parse(cancelled.ToString());
            Assert.AreEqual("cancelled", message.RootElement.GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("--prepare-update")]
    [DataRow("--install-package")]
    [DataRow("--restart-update")]
    [DataRow("--inspect-update")]
    public async Task RealHelperProcessExitsInsteadOfStartingMcpForMalformedInvocation(string mode)
    {
        var start = StartInfo();
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(1, process.ExitCode, await stderr);
            using var result = JsonDocument.Parse(await stdout);
            Assert.AreEqual("failed", result.RootElement.GetProperty("status").GetString());
            Assert.IsFalse(result.RootElement.GetProperty(mode switch
            { "--install-package" => "automaticUpdatingQualified", "--restart-update" => "nativeEditorRestarted",
                "--inspect-update" => "automaticRecoveryAvailable", _ => "installationReady" }).GetBoolean());
            Assert.IsFalse(result.RootElement.TryGetProperty("jsonrpc", out _));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [TestMethod]
    public async Task BootstrapRejectsEmbeddedTrustKeysAndMalformedRequests()
    {
        string root = Directory.CreateTempSubdirectory("kicad-bootstrap-input-").FullName;
        try
        {
            string request = Path.Combine(root, "request.json");
            string key = Path.Combine(root, "operator-key.spki");
            foreach (string json in new[] { "broken", "{}", "{\"schemaVersion\":1,\"schemaVersion\":2}",
                "{\"schemaVersion\":1,\"publisherKeySpki\":\"untrusted-request-key\"}" })
            {
                await File.WriteAllTextAsync(request, json);
                using var output = new StringWriter();
                Assert.AreEqual(1, await LinuxInstallCommand.RunAsync(
                    ["--install-package", "--configuration", request, "--publisher-key", key], output, CancellationToken.None));
                using var result = JsonDocument.Parse(output.ToString());
                Assert.AreEqual("failed", result.RootElement.GetProperty("status").GetString());
                Assert.IsFalse(result.RootElement.GetProperty("automaticUpdatingQualified").GetBoolean());
                Assert.IsEmpty(Directory.GetDirectories(root));
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var cancelled = new StringWriter();
            Assert.AreEqual(2, await LinuxInstallCommand.RunAsync(
                ["--install-package", "--configuration", request, "--publisher-key", key], cancelled, cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static ProcessStartInfo StartInfo()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Run this source test from the automation checkout.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll"));
        return start;
    }
}
