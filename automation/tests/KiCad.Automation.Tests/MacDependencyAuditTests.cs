using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacDependencyAuditTests
{
    [TestMethod]
    public async Task AuditSeparatesNativeAndManagedLoaderContextsAndCannotPretendLinuxIsMac()
    {
        string root = Directory.CreateTempSubdirectory("kicad-mac-deps-").FullName;
        try
        {
            string bundle = Path.Combine(root, "KiCad.app"), managed = Path.Combine(root, "managed");
            foreach (string name in new[] { "kicad", "kicad-cli", "dxf2idf", "idf2vrml", "idfcyl", "idfrect" })
                await Fixture(Path.Combine(bundle, "Contents/MacOS", name));
            foreach (string name in new[] { "eeschema", "pcbnew", "gerbview", "bitmap2component", "pcb_calculator", "pl_editor" })
                await Fixture(Path.Combine(bundle, "Contents/Applications", name + ".app", "Contents/MacOS", name));
            await Fixture(Path.Combine(bundle, "Contents/Frameworks/libnng.1.dylib"));
            await Fixture(Path.Combine(bundle, "Contents/PlugIns/test.kiface"));
            await Fixture(Path.Combine(managed, "kicad-mcp"));
            await Fixture(Path.Combine(managed, "libcoreclr.dylib"));
            string output = Path.Combine(root, "dependencies.json");
            string script = MacDependencyAudit.CreateScript(bundle, managed, output);
            StringAssert.Contains(script, "BUNDLE_EXECUTABLE \"${main}\"");
            StringAssert.Contains(script, "native_modules");
            StringAssert.Contains(script, "test.kiface");
            StringAssert.Contains(script, "managed_libraries");
            StringAssert.Contains(script, "libnng.1.dylib");
            StringAssert.Contains(script, "unresolved OR conflict_FILENAMES");
            StringAssert.Contains(script, "file(REAL_PATH \"${dependency}\" actual)");
            StringAssert.Contains(script, "set(reported \"${native_bundle}/${relative}\")");
            StringAssert.Contains(script, "set(reported \"${managed_root}/${relative}\")");
            Assert.IsFalse(script.Contains("DIRECTORIES ", StringComparison.Ordinal));
            if (!OperatingSystem.IsMacOS())
            {
                // This executes only the platform guard on Linux. The fixture
                // files are synthetic, not Mach-O/native dependency evidence.
                string path = Path.Combine(root, "audit.cmake");
                await File.WriteAllTextAsync(path, script);
                var start = new ProcessStartInfo("cmake")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-P"); start.ArgumentList.Add(path);
                using var process = Process.Start(start)!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                    Assert.AreNotEqual(0, process.ExitCode);
                    StringAssert.Contains(await stderr, "must execute on macOS");
                    Assert.IsFalse(File.Exists(output));
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                }
            }
            Assert.ThrowsExactly<ArgumentException>(() => MacDependencyAudit.CreateScript(bundle, managed, root + ";other"));
            File.Delete(Path.Combine(bundle, "Contents/MacOS/kicad-cli"));
            Assert.ThrowsExactly<FileNotFoundException>(() => MacDependencyAudit.CreateScript(bundle, managed, output));
        }
        finally { Directory.Delete(root, recursive: true); }

        static async Task Fixture(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "Synthetic dependency-script generation fixture.");
        }
    }

    [TestMethod]
    public void ReportRejectsMissingGroupsForeignRootsAndDeveloperMachineLibraries()
    {
        const string bundle = "/candidate/KiCad.app", managed = "/candidate/managed";
        var native = new MacDependencyGroup("native", [bundle + "/Contents/MacOS/kicad"],
            [bundle + "/Contents/Frameworks/libnng.dylib"]);
        var runtime = new MacDependencyGroup("managed", [managed + "/kicad-mcp", managed + "/libcoreclr.dylib"],
            [bundle + "/Contents/Frameworks/libnng.dylib"]);
        var report = new MacDependencyReport(1, bundle, managed, [native, runtime]);
        string Json(MacDependencyReport value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.HasCount(2, MacDependencyAudit.ReadReport(Json(report), bundle, managed).Groups);
        foreach (var invalid in new[]
        {
            report with { NativeBundle = "/other/KiCad.app" }, report with { Groups = [native] },
            report with { Groups = [native, native] },
            report with { Groups = [native, runtime with { Resolved = ["/opt/homebrew/lib/libnng.dylib"] }] },
            report with { Groups = [native, runtime with { Resolved = [managed + "/../../unbundled.dylib"] }] },
            report with { Groups = [native, runtime with { Roots = [] }] },
            report with { Groups = [native, runtime with { Resolved = ["relative.dylib"] }] }
        }) Assert.ThrowsExactly<InvalidDataException>(() => MacDependencyAudit.ReadReport(Json(invalid), bundle, managed));
    }
}
