using System.Text.Json;
using System.Diagnostics;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacUiDriverTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RecordNativeUiAccessWithoutRequestingPermissions()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Native Mac UI capability observation."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-mac-ui-capability-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "mac-ui-capability")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/mac-ui-driver.mm"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string executable = Path.Combine(root, "mac-ui-driver");
            _ = await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-framework", "AppKit", "-framework", "ApplicationServices",
                Path.Combine(repository.FullName, "automation/tests/fixtures/mac-ui-driver.mm"), "-o", executable], deadline.Token);
            string output = await MacProcessIdentityTests.Run(executable, ["capabilities"], deadline.Token);
            using var result = JsonDocument.Parse(output);
            Assert.AreEqual(1, result.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.IsFalse(result.RootElement.GetProperty("permissionsChanged").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(evidence, "capabilities.json"), output, deadline.Token);
            TestContext.AddResultFile(Path.Combine(evidence, "capabilities.json"));
            TestContext.WriteLine("Capability observation only, not a rendered KiCad journey: " + output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DriverPressesAndCapturesAnExactExternalNativeControl()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Native external-control journey requires macOS."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-external-ui-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "mac-external-ui-control")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Process? process = null;
        try
        {
            var ui = await MacUiAutomation.CreateAsync(root, evidence, deadline.Token);
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-action.mm"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string executable = Path.Combine(root, "caption-control");
            string platform = Path.Combine(repository.FullName, "libs/kiplatform/port/wxosx");
            await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-framework", "AppKit", "-I" + platform,
                Path.Combine(platform, "caption_action.mm"), Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-action.mm"),
                "-o", executable], deadline.Token);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("--external-ui"); start.ArgumentList.Add(root);
            process = Process.Start(start)!;
            while (!File.Exists(Path.Combine(root, "ready")))
            {
                Assert.IsFalse(process.HasExited);
                await Task.Delay(100, deadline.Token);
            }
            var identity = MacProcessIdentity.Read(process.Id);
            await ui.WaitButtonAsync(identity, "Update", deadline.Token);
            await ui.CaptureAsync(identity, "external-control", deadline.Token);
            await Assert.ThrowsAsync<AssertFailedException>(() => ui.PressAsync(identity with
                { StartMicroseconds = (identity.StartMicroseconds + 1) % 1000000 }, "Update", deadline.Token));
            Assert.IsFalse(File.Exists(Path.Combine(root, "pressed.json")));
            await Assert.ThrowsAsync<AssertFailedException>(() => ui.PressAsync(identity, "Missing target", deadline.Token));
            Assert.IsFalse(File.Exists(Path.Combine(root, "pressed.json")));
            await ui.PressAsync(identity, "Update", deadline.Token);
            while (!File.Exists(Path.Combine(root, "pressed.json"))) await Task.Delay(50, deadline.Token);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "pressed.json"), deadline.Token));
            Assert.IsTrue(result.RootElement.GetProperty("pressed").GetBoolean());
            Assert.IsFalse(result.RootElement.GetProperty("applicationUpdateJourneyVerified").GetBoolean());
            File.Copy(Path.Combine(root, "pressed.json"), Path.Combine(evidence, "result.json"));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            foreach (string path in Directory.GetFiles(evidence)) TestContext.AddResultFile(path);
            Directory.Delete(root, recursive: true);
        }
    }
}
