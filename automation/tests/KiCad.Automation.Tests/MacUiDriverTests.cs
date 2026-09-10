using System.Text.Json;
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
}
