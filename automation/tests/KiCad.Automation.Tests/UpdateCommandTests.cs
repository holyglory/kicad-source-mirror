using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateCommandTests
{
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
    public async Task RealHelperProcessExitsInsteadOfStartingMcpForMalformedInvocation()
    {
        var start = StartInfo();
        start.ArgumentList.Add("--prepare-update");
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
            Assert.IsFalse(result.RootElement.GetProperty("installationReady").GetBoolean());
            Assert.IsFalse(result.RootElement.TryGetProperty("jsonrpc", out _));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
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
