using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacCaptionActionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task IndependentNativeImagesShareOneRuntimeClassWithoutMixingCallbacks()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Requires native Objective-C runtime loading."); return; }
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-modules.mm"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string root = Directory.CreateTempSubdirectory("kicad-caption-modules-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            string platform = Path.Combine(repository.FullName, "libs/kiplatform/port/wxosx");
            foreach (string name in new[] { "first", "second" })
                await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-dynamiclib", "-framework", "AppKit", "-I" + platform,
                    Path.Combine(platform, "caption_action.mm"), Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-module.mm"),
                    "-o", Path.Combine(root, name + ".dylib")], deadline.Token);
            string executable = Path.Combine(root, "modules");
            await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-framework", "AppKit",
                Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-modules.mm"), "-o", executable], deadline.Token);
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "first.dylib")); start.ArgumentList.Add(Path.Combine(root, "second.dylib"));
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            Assert.AreEqual(0, process.ExitCode, await stderr);
            Assert.IsFalse((await stderr).Contains("implemented in both", StringComparison.Ordinal));
            StringAssert.Contains(await stdout, "\"independentCallbacks\":true");
            string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
                ?? TestContext.TestResultsDirectory!, "mac-caption-modules")).FullName;
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(evidence, "stderr.log"), await stderr);
            TestContext.AddResultFile(Path.Combine(evidence, "result.json")); TestContext.AddResultFile(Path.Combine(evidence, "stderr.log"));
            foreach (string name in new[] { "bad-first", "bad-second" })
                await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-dynamiclib", "-framework", "AppKit", "-I" + platform,
                    "-DKICAD_DUPLICATE_CLASS_CONTROL", Path.Combine(platform, "caption_action.mm"),
                    Path.Combine(repository.FullName, "automation/tests/fixtures/mac-caption-module.mm"), "-o", Path.Combine(root, name + ".dylib")], deadline.Token);
            start.ArgumentList.Clear(); start.ArgumentList.Add(Path.Combine(root, "bad-first.dylib")); start.ArgumentList.Add(Path.Combine(root, "bad-second.dylib"));
            using var negative = System.Diagnostics.Process.Start(start)!;
            var negativeOut = negative.StandardOutput.ReadToEndAsync(); var negativeError = negative.StandardError.ReadToEndAsync();
            try { await negative.WaitForExitAsync(deadline.Token); }
            finally { if(!negative.HasExited) negative.Kill(true); await negative.WaitForExitAsync(); }
            Assert.AreEqual(0, negative.ExitCode, await negativeOut);
            StringAssert.Contains(await negativeError, "implemented in both", "The duplicate-class detector must recognize an actual native duplicate.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "negative.stderr.log"), await negativeError);
            TestContext.AddResultFile(Path.Combine(evidence, "negative.stderr.log"));
        }
        finally { Directory.Delete(root, true); }
    }

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
