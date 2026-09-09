using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RuntimeInfoTests
{
    [TestMethod]
    [DataRow("inherited", true)]
    [DataRow("explicit", true)]
    [DataRow("relative", false)]
    [DataRow("missing", false)]
    [DataRow("invalid", false)]
    [DataRow("extra-argument", false)]
    public async Task RealCompiledProbeReportsNativeRuntimeOrExplicitFailure(string mode, bool succeeds)
    {
        string root = Directory.CreateTempSubdirectory("kicad-runtime-info-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = UpdateCommandTests.StartInfo();
        start.ArgumentList.Add("--runtime-info");
        try
        {
            if (mode == "explicit")
            {
                string? library = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY")
                    ?? Environment.GetEnvironmentVariable("KICAD_TEST_NNG_LIBRARY");
                if (library is null) { Assert.Inconclusive("Select the existing native NNG library for the explicit-path fixture."); return; }
                start.Environment["KICAD_AUTOMATION_NNG_LIBRARY"] = library;
            }
            if (mode == "relative") start.Environment["KICAD_AUTOMATION_NNG_LIBRARY"] = "relative-nng";
            if (mode == "missing") start.Environment["KICAD_AUTOMATION_NNG_LIBRARY"] = Path.Combine(root, "absent-nng");
            if (mode == "invalid")
            {
                string invalid = Path.Combine(root, "not-a-library");
                await File.WriteAllTextAsync(invalid, "Synthetic invalid native library.", deadline.Token);
                start.Environment["KICAD_AUTOMATION_NNG_LIBRARY"] = invalid;
            }
            if (mode == "extra-argument") start.ArgumentList.Add("unexpected");
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(succeeds ? 0 : 1, process.ExitCode, await stderr);
                using var result = JsonDocument.Parse(await stdout);
                Assert.AreEqual(succeeds ? "runtime_available" : "failed", result.RootElement.GetProperty("status").GetString());
                Assert.IsFalse(result.RootElement.GetProperty("nativeEditorContacted").GetBoolean());
                Assert.IsFalse(result.RootElement.GetProperty("crossPlatformReady").GetBoolean());
                if (succeeds)
                {
                    Assert.AreEqual(RuntimeInformation.ProcessArchitecture.ToString(), result.RootElement.GetProperty("processArchitecture").GetString());
                    Assert.IsFalse(string.IsNullOrWhiteSpace(result.RootElement.GetProperty("nngVersion").GetString()));
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CancellationBeforeProbeReturnsNoEditorContact()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        Assert.AreEqual(2, await RuntimeInfoCommand.RunAsync(["--runtime-info"], output, cancellation.Token));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.AreEqual("cancelled", result.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(result.RootElement.GetProperty("nativeEditorContacted").GetBoolean());
    }
}
