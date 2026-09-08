using System.Diagnostics;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxPackageTests
{
    [TestMethod]
    public async Task ManagedLauncherDiscoversVersionContextWithoutOverridingExplicitSettings()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux launcher test."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-managed-launcher-Énergie space-").FullName;
        try
        {
            string version = Directory.CreateDirectory(Path.Combine(root, "versions", new string('1', 64))).FullName;
            string payload = Directory.CreateDirectory(Path.Combine(version, "payload")).FullName;
            Directory.CreateDirectory(Path.Combine(payload, "runtime/bin"));
            string probe = Path.Combine(payload, "runtime/bin/probe");
            File.Copy("/usr/bin/printenv", probe);
            File.SetUnixFileMode(probe, (UnixFileMode)0x1ED);
            await LinuxPackage.WriteLauncherAsync(payload, "kicad-codex", "runtime/bin/probe", CancellationToken.None);
            await LinuxPackage.WriteLauncherAsync(payload, "kicad-mcp", "runtime/bin/probe", CancellationToken.None);
            Assert.AreEqual((1, ""), await Run("kicad-codex"));
            // Synthetic layout markers test discovery only; the actual signed
            // bootstrap and configuration are verified in the installed journey.
            foreach (string marker in new[] { "version.json", "installed-envelope.json", "update-config.json" })
                await File.WriteAllTextAsync(Path.Combine(version, marker), "{}");
            Assert.AreEqual((1, ""), await Run("kicad-codex"));
            await File.WriteAllTextAsync(Path.Combine(root, "publisher.json"), "{}");
            string expected = Path.Combine(payload, "runtime/lib/kicad-automation/kicad-mcp") + "\n"
                + Path.Combine(version, "update-config.json") + "\n";
            Assert.AreEqual((0, expected), await Run("kicad-codex"));
            Assert.AreEqual((1, ""), await Run("kicad-mcp"));
            Assert.AreEqual((0, "/explicit/helper\n/explicit/configuration\n"),
                await Run("kicad-codex", "/explicit/helper", "/explicit/configuration"));
            Assert.AreEqual((1, "/explicit/helper\n"), await Run("kicad-codex", "/explicit/helper"));
            Directory.Move(Path.Combine(root, "versions"), Path.Combine(root, "adjacent-documents"));
            payload = Path.Combine(root, "adjacent-documents", new string('1', 64), "payload");
            Assert.AreEqual((1, ""), await Run("kicad-codex"));

            async Task<(int ExitCode, string Output)> Run(string name, string? helper = null, string? configuration = null)
            {
                var start = new ProcessStartInfo(Path.Combine(payload, name))
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment.Remove("KICAD_AUTOMATION_UPDATE_HELPER");
                start.Environment.Remove("KICAD_AUTOMATION_UPDATE_CONFIG");
                if (helper is not null) start.Environment["KICAD_AUTOMATION_UPDATE_HELPER"] = helper;
                if (configuration is not null) start.Environment["KICAD_AUTOMATION_UPDATE_CONFIG"] = configuration;
                start.ArgumentList.Add("KICAD_AUTOMATION_UPDATE_HELPER");
                start.ArgumentList.Add("KICAD_AUTOMATION_UPDATE_CONFIG");
                using var process = Process.Start(start)!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                    Assert.AreEqual("", await stderr);
                    return (process.ExitCode, await stdout);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LauncherUsesPhysicalVersionPathsWhenStartedThroughCurrentLink()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux launchers require Linux."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-launcher-Énergie space-").FullName;
        try
        {
            string version = Directory.CreateDirectory(Path.Combine(root, "version-one")).FullName;
            Directory.CreateDirectory(Path.Combine(version, "runtime/bin"));
            string probe = Path.Combine(version, "runtime/bin/probe");
            // A compiled environment-printing test double, not a simulated
            // native editor. Real editor launch remains a separate journey.
            File.Copy("/usr/bin/printenv", probe);
            File.SetUnixFileMode(probe, (UnixFileMode)0x1ED);
            await LinuxPackage.WriteLauncherAsync(version, "kicad-codex", "runtime/bin/probe", CancellationToken.None);
            string current = Path.Combine(root, "current");
            Directory.CreateSymbolicLink(current, "version-one");
            var start = new ProcessStartInfo(Path.Combine(current, "kicad-codex"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("LD_LIBRARY_PATH");
            start.ArgumentList.Add("KICAD_STOCK_DATA_HOME");
            using var process = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await stderr);
                CollectionAssert.AreEqual(new[] { Path.Combine(version, "runtime/lib"), Path.Combine(version, "runtime/share/kicad") },
                    (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RelativeInventoryPathsCannotEscapeTheirPackage()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "synthetic-package-root"));
        Assert.AreEqual(Path.Combine(root, "lib", "library.so"),
            LinuxPackage.ContainedPath(root, "lib/library.so"));
        Assert.ThrowsExactly<InvalidDataException>(() => LinuxPackage.ContainedPath(root, "../private"));
        Assert.ThrowsExactly<InvalidDataException>(() => LinuxPackage.ContainedPath(root, root));
    }

    [TestMethod]
    public async Task SourceExportRequiresExactCommittedCodeAndExcludesPrivateContext()
    {
        string root = Directory.CreateTempSubdirectory("kicad-source-fixture-").FullName;
        try
        {
            await Git("init", "--initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "Synthetic source fixture.");
            await Git("add", "source.txt");
            await Commit();
            string commit = (await Git("rev-parse", "HEAD")).Trim();
            await LinuxPackage.RequireSourceAsync(root, commit, CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(root, "automation/reports"));
            await File.WriteAllTextAsync(Path.Combine(root, "automation/reports/private.txt"), "Private fixture.");
            Directory.CreateDirectory(Path.Combine(root, "automation/.serena"));
            await File.WriteAllTextAsync(Path.Combine(root, "automation/.serena/project.yml"), "Private navigation fixture.");
            await LinuxPackage.RequireSourceAsync(root, commit, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxPackage.RequireSourceAsync(root, new string('0', 40), CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(root, "new-source.txt"), "Uncommitted fixture source.");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxPackage.RequireSourceAsync(root, commit, CancellationToken.None));
            File.Delete(Path.Combine(root, "new-source.txt"));
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "Changed source fixture.");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxPackage.RequireSourceAsync(root, commit, CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "Synthetic source fixture.");
            await LinuxPackage.RequireSourceAsync(root, commit, CancellationToken.None);
            await Git("add", "automation/.serena/project.yml");
            await Commit();
            string privateCommit = (await Git("rev-parse", "HEAD")).Trim();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxPackage.RequireSourceAsync(root, privateCommit, CancellationToken.None));
            await Git("add", "automation/reports/private.txt");
            await Commit();
            privateCommit = (await Git("rev-parse", "HEAD")).Trim();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LinuxPackage.RequireSourceAsync(root, privateCommit, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }

        Task<string> Commit() => Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m", "Synthetic package-source fixture");

        async Task<string> Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            Assert.AreEqual(0, process.ExitCode, await stderr);
            return await stdout;
        }
    }
}
