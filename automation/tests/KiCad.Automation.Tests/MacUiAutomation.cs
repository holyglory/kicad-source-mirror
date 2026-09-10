using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>Native Accessibility actions scoped to one exact test process. No permission grants.</summary>
internal sealed class MacUiAutomation(string executable, string scratch, string evidence)
{
    public static async Task<MacUiAutomation> CreateAsync(string scratch, string evidence, CancellationToken token)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/mac-ui-driver.mm"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string driver = Path.Combine(scratch, "mac-ui-driver");
        await MacProcessIdentityTests.Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-framework", "AppKit", "-framework", "ApplicationServices",
            Path.Combine(repository.FullName, "automation/tests/fixtures/mac-ui-driver.mm"), "-o", driver], token);
        using var capabilities = JsonDocument.Parse(await MacProcessIdentityTests.Run(driver, ["capabilities"], token));
        Assert.IsTrue(capabilities.RootElement.GetProperty("accessibilityTrusted").GetBoolean(), "Native Accessibility is unavailable; do not grant it automatically.");
        Assert.IsTrue(capabilities.RootElement.GetProperty("screenCaptureAllowed").GetBoolean(), "Native screen capture is unavailable; do not grant it automatically.");
        return new(driver, scratch, evidence);
    }

    public async Task<JsonElement> InspectAsync(MacProcessIdentity process, CancellationToken token) =>
        await Invoke("inspect", process, null, token);

    public async Task PressAsync(MacProcessIdentity process, string title, CancellationToken token)
    {
        var result = await Invoke("press", process, title, token);
        Assert.AreEqual("action_sent", result.GetProperty("status").GetString());
    }

    public async Task WaitButtonAsync(MacProcessIdentity process, string title, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        string? previous = null;
        int delay = 50;
        while (true)
        {
            var observation = await InspectAsync(process, deadline.Token);
            var buttons = observation.GetProperty("buttons");
            if (buttons.EnumerateArray().Count(x => x.GetProperty("title").GetString() == title
                && x.GetProperty("enabled").GetBoolean()) == 1) return;
            string current = buttons.GetRawText();
            delay = current == previous ? Math.Min(delay * 2, 250) : 50;
            previous = current;
            await Task.Delay(delay, deadline.Token);
        }
    }

    public async Task CaptureAsync(MacProcessIdentity process, string name, CancellationToken token)
    {
        var observation = await InspectAsync(process, token);
        var windows = observation.GetProperty("windows").EnumerateArray().ToArray();
        Assert.IsTrue(windows.Length > 0, "No visible window belongs to the expected process.");
        int index = 0;
        foreach (var window in windows.Take(4))
        {
            string path = Path.Combine(evidence, name + "-" + index++ + ".png");
            await MacProcessIdentityTests.Run("/usr/sbin/screencapture", ["-x", "-o", "-l", window.GetProperty("id").ToString(), path], token);
            byte[] image = await File.ReadAllBytesAsync(path, token);
            Assert.IsTrue(image.Length >= 24 && image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), observation.GetRawText(), token);
    }

    private async Task<JsonElement> Invoke(string operation, MacProcessIdentity process, string? title, CancellationToken token)
    {
        Assert.AreEqual(process, MacProcessIdentity.Read(process.ProcessId), "The UI target changed before the action.");
        string path = Path.Combine(scratch, "ui-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, process.ProcessId, process.BootId, process.StartSeconds, process.StartMicroseconds,
                process.Executable, title
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
            string output = await MacProcessIdentityTests.Run(executable, [operation, path], token);
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
