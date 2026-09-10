using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsUiDriverTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CaptureSaveAndCloseAnExactExternalNativeWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires real Windows UI, input and GDI capture.");
            return;
        }
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/windows-native-ui.cpp")))
            repository = repository.Parent;
        Assert.IsNotNull(repository);
        string scratch = Directory.CreateTempSubdirectory("kwui-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "windows-native-ui")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? windowProcess = null;
        try
        {
            string executable = Path.Combine(scratch, "window.exe");
            var compiler = new ProcessStartInfo("cl.exe") { WorkingDirectory = scratch, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/nologo", "/EHsc", "/std:c++17",
                Path.Combine(repository.FullName, "automation/tests/fixtures/windows-native-ui.cpp"),
                "/Fe:" + executable, "user32.lib", "gdi32.lib" }) compiler.ArgumentList.Add(argument);
            using (var process = Process.Start(compiler)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(deadline.Token); }
                finally
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync();
                    await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stdout.log"), await stdout);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stderr.log"), await stderr);
                }
                Assert.AreEqual(0, process.ExitCode);
            }
            string saved = Path.Combine(scratch, "saved.txt");
            var start = new ProcessStartInfo(executable) { WorkingDirectory = scratch, UseShellExecute = false };
            start.ArgumentList.Add(saved);
            windowProcess = Process.Start(start)!;
            nint window = await WindowsNativeUi.WaitForWindow(windowProcess, "KiCad native UI fixture", deadline.Token);
            using (var cancelledWait = new CancellationTokenSource(100))
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    WindowsNativeUi.WaitForWindow(windowProcess, "No such fixture window", cancelledWait.Token));
            using var wrongOwner = Process.GetCurrentProcess();
            Assert.ThrowsExactly<InvalidOperationException>(() => WindowsNativeUi.Save(wrongOwner, window));
            Assert.IsFalse(File.Exists(saved), "A wrong process target must not send keyboard input.");
            string before = Path.Combine(evidence, "before.bmp"), after = Path.Combine(evidence, "after.bmp");
            WindowsNativeUi.Capture(windowProcess, window, before);
            WindowsNativeUi.Save(windowProcess, window);
            Assert.AreEqual(window, await WindowsNativeUi.WaitForWindow(windowProcess, "fixture - Saved", deadline.Token));
            Assert.AreEqual("saved by native Ctrl+S", await File.ReadAllTextAsync(saved, deadline.Token));
            WindowsNativeUi.Capture(windowProcess, window, after);
            byte[] beforeBytes = await File.ReadAllBytesAsync(before), afterBytes = await File.ReadAllBytesAsync(after);
            Assert.IsFalse(beforeBytes.AsSpan().SequenceEqual(afterBytes),
                "The captured native window must reflect the saved state.");
            WindowsNativeUi.Shortcut(windowProcess, window, 0x11, 0x42); // Fixture Ctrl+B makes its client area blank.
            await WindowsNativeUi.WaitForWindow(windowProcess, "fixture - Blank", deadline.Token);
            string blank = Path.Combine(evidence, "rejected-blank.bmp");
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsNativeUi.Capture(windowProcess, window, blank));
            Assert.IsFalse(File.Exists(blank));
            WindowsNativeUi.Shortcut(windowProcess, window, 0x11, 0x42);
            await WindowsNativeUi.WaitForWindow(windowProcess, "fixture - Saved", deadline.Token);
            WindowsNativeUi.Close(windowProcess, window);
            await windowProcess.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, windowProcess.ExitCode);
            Assert.ThrowsExactly<InvalidOperationException>(() => WindowsNativeUi.Save(windowProcess, window));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"),
                "{\"schemaVersion\":1,\"nativeInputAndCaptureVerified\":true,\"kicadPackageJourneyVerified\":false,\"permissionsChanged\":false}");
            TestContext.AddResultFile(before); TestContext.AddResultFile(after);
            TestContext.AddResultFile(Path.Combine(evidence, "result.json"));
        }
        finally
        {
            if (windowProcess is not null)
            {
                if (!windowProcess.HasExited) windowProcess.Kill(true);
                await windowProcess.WaitForExitAsync(); windowProcess.Dispose();
            }
            Directory.Delete(scratch, true);
        }
    }
}
