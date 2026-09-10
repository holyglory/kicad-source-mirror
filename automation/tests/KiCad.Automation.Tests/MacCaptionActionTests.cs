using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacCaptionActionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeCaptionButtonInvokesOnlyItsOwnerAndRespectsVisibilityAndEnabledState()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("This control fixture requires native AppKit rendering."); return; }
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "libs/kiplatform/port/wxosx/caption_action.mm"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string root = Directory.CreateTempSubdirectory("kicad-caption-native-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "mac-caption-control")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            string executable = Path.Combine(root, "caption-control");
            string platform = Path.Combine(repository.FullName, "libs/kiplatform/port/wxosx");
            _ = await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-framework", "AppKit",
                "-I" + platform, Path.Combine(platform, "caption_action.mm"),
                Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-action.mm"), "-o", executable], deadline.Token);
            string result = await MacProcessIdentityTests.Run(executable, [evidence], deadline.Token);
            StringAssert.Contains(result, "\"renderedControlVerified\":true");
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), result, deadline.Token);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(evidence)) TestContext.AddResultFile(file);
            Directory.Delete(root, recursive: true);
        }
    }
}
