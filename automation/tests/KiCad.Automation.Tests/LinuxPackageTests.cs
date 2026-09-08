using System.Diagnostics;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxPackageTests
{
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
