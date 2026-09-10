using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NngBindingLifetimeTests
{
    [TestMethod]
    public async Task IsolatedNativeBindingSurvivesRemovalOfItsSelectedPath()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Unix deleted-path fixture."); return; }
        string? library = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY")
            ?? Environment.GetEnvironmentVariable("KICAD_TEST_NNG_LIBRARY");
        if (library is null && OperatingSystem.IsLinux()) library = "/usr/lib/x86_64-linux-gnu/libnng.so";
        if (library is null) { Assert.Inconclusive("The native Mac handoff fixture supplies its actual bundled library."); return; }
        await RunProbe(library, CancellationToken.None);
    }

    internal static async Task RunProbe(string library, CancellationToken token)
    {
        await RunChild(library, false, token);
        if (OperatingSystem.IsMacOS()) await RunChild(library, true, token);
    }

    private static async Task RunChild(string library, bool negative, CancellationToken token)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { Path.Combine(root.FullName, "tools/KiCad.Automation.PackageProbe/bin", configuration,
            "net10.0/kicad-package-probe.dll"), "--nng-binding-lifetime", library }) start.ArgumentList.Add(argument);
        if (negative) start.ArgumentList.Add("--reopen-path");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token), stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string output = await stdout;
            Assert.AreEqual(negative ? 2 : 0, process.ExitCode, output + await stderr);
            using var result = JsonDocument.Parse(output);
            Assert.AreEqual(negative ? "expected_missing_library" : "passed", result.RootElement.GetProperty("status").GetString());
            if (negative) return;
            Assert.IsTrue(result.RootElement.GetProperty("selectedPathRemoved").GetBoolean());
            Assert.IsTrue(result.RootElement.GetProperty("lateNativeBindingsVerified").GetBoolean());
            Assert.IsTrue(result.RootElement.GetProperty("originalLibraryPreserved").GetBoolean());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}
