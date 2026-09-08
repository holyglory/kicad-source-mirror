using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyFailedStartup(string executable, string temporary, string evidence,
        CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Combine(temporary, "failed-start")).FullName;
        string project = Path.Combine(directory, "failure.kicad_pro");
        await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", token);
        var registry = new InstanceRegistry(new NngTransport(), Path.Combine(directory, "registry"), start =>
        {
            // Empty DISPLAY cannot connect to the test's separate X server.
            start.Environment["DISPLAY"] = "";
            start.Environment.Remove("WAYLAND_DISPLAY");
            start.Environment["GDK_BACKEND"] = "x11";
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
        });
        var error = await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.StartAsync(executable, project, token));
        Assert.AreEqual("start_failed", error.Code);
        Assert.AreEqual(0, registry.List().Count);
        Assert.AreEqual(0, (await registry.SavedSessionsAsync(token)).Count);
        var launch = (await registry.PendingLaunchesAsync(token)).Single();
        Assert.AreEqual(project, launch.ProjectPath);
        Assert.IsNotNull(launch.ProcessId);
        string runtime = Path.GetDirectoryName(launch.Endpoint["ipc://".Length..])!;
        StringAssert.Contains(error.Message, runtime);
        foreach (string name in new[] { "native.log", "bootstrap.stdout.log", "bootstrap.stderr.log" })
            if (File.Exists(Path.Combine(runtime, name)))
                File.Copy(Path.Combine(runtime, name), Path.Combine(evidence, "failed-start-" + name), true);
        Assert.IsTrue(Directory.EnumerateFiles(evidence, "failed-start-*.log").Any(),
            "Failed startup must retain native or bootstrap diagnostics.");
    }
}
