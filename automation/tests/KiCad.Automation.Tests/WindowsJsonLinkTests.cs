using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsJsonLinkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void UpdaterHeaderUsesTheExistingSharedJsonImports()
    {
        string header = File.ReadAllText(Path.Combine(Repository(), "kicad/automation_update_client.h"));
        StringAssert.Contains(header, "#include <json_common.h>");
        Assert.IsFalse(header.Contains("#include <nlohmann/json.hpp>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WindowsDllConsumerRejectsDuplicateDefinitionsAndRunsWithKiCadImports()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This check requires the native MSVC compiler and Windows DLL loader.");
            return;
        }
        string root = Directory.CreateTempSubdirectory("kicad-json-link-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(TestContext.TestResultsDirectory!, "windows-json-link")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            Assert.AreEqual(0, await Run("configure", "cmake", ["-S", Path.Combine(Repository(), "automation/tests/fixtures/windows-json-link"),
                "-B", root, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release"]));
            Assert.AreEqual(0, await Run("wrapped-build", "cmake", ["--build", root, "--target", "wrapped"]));
            Assert.AreEqual(0, await Run("wrapped-execution", Path.Combine(root, "wrapped.exe"), []));
            Assert.AreNotEqual(0, await Run("raw-build", "cmake", ["--build", root, "--target", "raw"]),
                "The negative fixture did not reproduce the duplicate JSON definition failure.");
            string diagnostic = await File.ReadAllTextAsync(Path.Combine(evidence, "raw-build.stdout.log"));
            StringAssert.Contains(diagnostic, "LNK2005", "A different build failure cannot prove this regression.");
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task<int> Run(string name, string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start " + name);
            await using var stdout = File.Create(Path.Combine(evidence, name + ".stdout.log"));
            await using var stderr = File.Create(Path.Combine(evidence, name + ".stderr.log"));
            Task output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task error = process.StandardError.BaseStream.CopyToAsync(stderr);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
            }
            return process.ExitCode;
        }
    }

    private static string Repository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "kicad/automation_update_client.h")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The KiCad source fixture is unavailable.");
    }
}
