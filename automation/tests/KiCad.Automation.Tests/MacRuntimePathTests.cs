using System.Diagnostics;
using System.Runtime.InteropServices;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("NativeMac")]
public sealed class MacRuntimePathTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeImagesRelocateWithTransitiveDependenciesAndRejectAMissingLibrary()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("This fixture requires the real Mac compiler, loader and dependency resolver.");
            return;
        }
        string root = Directory.CreateTempSubdirectory("kicad-native-rpaths-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            string target = Environment.GetEnvironmentVariable("DELIVERY_ARCHITECTURE")
                ?? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
            MacArchitecture.RequireExecutionTarget(target, RuntimeInformation.ProcessArchitecture);
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "cmake/InstallSteps/RefixupMacOS.cmake")))
                repository = repository.Parent;
            Assert.IsNotNull(repository);
            string bundle = Path.Combine(root, "initial", "Fixture.app");
            string frameworks = Directory.CreateDirectory(Path.Combine(bundle, "Contents/Frameworks")).FullName;
            string bin = Directory.CreateDirectory(Path.Combine(bundle, "Contents/MacOS")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(root, "developer-libraries")).FullName;
            string leaf = Path.Combine(frameworks, "libleaf.dylib");
            string parent = Path.Combine(frameworks, "libparent.dylib");
            string executable = Path.Combine(bin, "fixture");
            await File.WriteAllTextAsync(Path.Combine(root, "leaf.c"), "int leaf(void) { return 7; }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "parent.c"), "extern int leaf(void); int parent(void) { return leaf(); }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.c"), "extern int parent(void); int main(void) { return parent() == 7 ? 0 : 1; }\n");
            await Run("xcrun", ["clang", "-dynamiclib", Path.Combine(root, "leaf.c"), "-o", leaf,
                "-Wl,-install_name,@rpath/libleaf.dylib", "-Wl,-headerpad_max_install_names"]);
            File.Copy(leaf, Path.Combine(outside, "libleaf.dylib"));
            await Run("xcrun", ["clang", "-dynamiclib", Path.Combine(root, "parent.c"), "-o", parent,
                "-L" + frameworks, "-lleaf", "-Wl,-install_name,@rpath/libparent.dylib",
                "-Wl,-rpath," + outside, "-Wl,-headerpad_max_install_names"]);
            await Run("xcrun", ["clang", Path.Combine(root, "main.c"), "-o", executable,
                "-L" + frameworks, "-lparent", "-Wl,-rpath," + frameworks, "-Wl,-headerpad_max_install_names"]);
            MacArchitecture.RequireBinaryTarget(target, (await Run("xcrun", ["lipo", "-archs", executable])).Trim());

            string fix = Path.Combine(root, "fix.cmake");
            string script = "cmake_minimum_required(VERSION 3.21)\ninclude(" + Q(Path.Combine(repository.FullName,
                "cmake/InstallSteps/RefixupMacOS.cmake")) + ")\n";
            string systemModule = Path.Combine(frameworks, "system-only.so");
            await Run("xcrun", ["clang", "-bundle", Path.Combine(root, "leaf.c"), "-o", systemModule]);
            string originalModuleHash = Evidence.Hash(systemModule);
            string originalLeafHash = Evidence.Hash(leaf);
            script += "refix_image_rpaths(" + Q(systemModule) + " " + Q(bundle) + ")\n";
            foreach (string file in new[] { leaf, parent, executable })
                script += "refix_image_rpaths(" + Q(file) + " " + Q(bundle) + ")\n";
            await File.WriteAllTextAsync(fix, script);
            await Run("cmake", ["-P", fix]);
            Assert.AreEqual(originalModuleHash, Evidence.Hash(systemModule), "A system-only module was needlessly changed.");
            Assert.AreEqual(originalLeafHash, Evidence.Hash(leaf), "A dylib self-install ID was mistaken for a dependency.");
            string first = await Run("otool", ["-l", parent]);
            Assert.IsFalse(first.Contains(outside, StringComparison.Ordinal), "A developer-library search path survived normalization.");
            await Run("cmake", ["-P", fix]);
            string second = await Run("otool", ["-l", parent]);
            Assert.AreEqual(first, second, "A second normalization changed the Mach-O load-command layout.");
            foreach (string file in new[] { leaf, parent, executable })
                await Run("codesign", ["--force", "--sign", "-", file]);

            string relocated = Path.Combine(root, "relocated", "Fixture.app");
            Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
            Directory.Move(bundle, relocated);
            string relocatedParent = Path.Combine(relocated, "Contents/Frameworks/libparent.dylib");
            await Run(Path.Combine(relocated, "Contents/MacOS/fixture"), []);
            string audit = Path.Combine(root, "audit.cmake");
            await File.WriteAllTextAsync(audit, "cmake_minimum_required(VERSION 3.21)\n"
                + "file(REAL_PATH " + Q(relocated) + " allowed)\n"
                + "file(GET_RUNTIME_DEPENDENCIES LIBRARIES " + Q(relocatedParent)
                + " RESOLVED_DEPENDENCIES_VAR resolved UNRESOLVED_DEPENDENCIES_VAR unresolved"
                + " PRE_EXCLUDE_REGEXES \"^/usr/lib/\" \"^/System/Library/\")\n"
                + "if(unresolved)\n message(FATAL_ERROR \"missing dependency: ${unresolved}\")\nendif()\n"
                + "foreach(path IN LISTS resolved)\n file(REAL_PATH \"${path}\" actual)\n"
                + " string(FIND \"${actual}\" \"${allowed}/\" prefix)\n"
                + " if(NOT prefix EQUAL 0)\n message(FATAL_ERROR \"unbundled: ${actual}\")\n endif()\nendforeach()\n");
            await Run("cmake", ["-P", audit]);
            File.Move(Path.Combine(relocated, "Contents/Frameworks/libleaf.dylib"), Path.Combine(root, "removed-leaf.dylib"));
            string missing = await Run("cmake", ["-P", audit], succeeds: false);
            StringAssert.Contains(missing, "missing dependency");
            // The original developer copy still exists: it must not hide the loss.
            Assert.IsTrue(File.Exists(Path.Combine(outside, "libleaf.dylib")));

            // Exercise the real audit with a KiCad-shaped synthetic bundle.
            // Each image is compiled above; these are not KiCad executables.
            File.Move(Path.Combine(root, "removed-leaf.dylib"), Path.Combine(relocated, "Contents/Frameworks/libleaf.dylib"));
            string relocatedExe = Path.Combine(relocated, "Contents/MacOS/fixture");
            foreach (string name in new[] { "kicad", "kicad-cli", "dxf2idf", "idf2vrml", "idfcyl", "idfrect" })
                File.Copy(relocatedExe, Path.Combine(relocated, "Contents/MacOS", name));
            foreach (string name in new[] { "eeschema", "pcbnew", "gerbview", "bitmap2component", "pcb_calculator", "pl_editor" })
            {
                string contents = Directory.CreateDirectory(Path.Combine(relocated, "Contents/Applications", name + ".app", "Contents")).FullName;
                Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
                File.Copy(relocatedExe, Path.Combine(contents, "MacOS", name));
                Directory.CreateSymbolicLink(Path.Combine(contents, "Frameworks"),
                    Path.GetRelativePath(contents, Path.Combine(relocated, "Contents/Frameworks")));
            }
            File.CreateSymbolicLink(Path.Combine(relocated, "Contents/Frameworks/libnng.1.dylib"), "libparent.dylib");
            string managed = Directory.CreateDirectory(Path.Combine(root, "managed")).FullName;
            await File.WriteAllTextAsync(Path.Combine(root, "empty.c"), "int main(void) { return 0; }\n");
            await Run("xcrun", ["clang", Path.Combine(root, "empty.c"), "-o", Path.Combine(managed, "kicad-mcp")]);
            string report = Path.Combine(root, "bundle-audit.json");
            string bundleAudit = Path.Combine(root, "bundle-audit.cmake");
            await File.WriteAllTextAsync(bundleAudit, MacDependencyAudit.CreateScript(relocated, managed, report));
            await Run("cmake", ["-P", bundleAudit]);
            Assert.HasCount(2, MacDependencyAudit.ReadReport(await File.ReadAllTextAsync(report), relocated, managed).Groups);

            // Same library names in genuinely different directories are not
            // aliases and must not be collapsed merely because names match.
            File.Copy(relocatedParent, Path.Combine(outside, "libparent.dylib"));
            string editorLink = Path.Combine(relocated, "Contents/Applications/eeschema.app/Contents/Frameworks");
            Directory.Delete(editorLink);
            Directory.CreateSymbolicLink(editorLink, outside);
            string conflictReport = Path.Combine(root, "conflicts.json");
            await File.WriteAllTextAsync(bundleAudit, MacDependencyAudit.CreateScript(relocated, managed, conflictReport));
            await Run("cmake", ["-P", bundleAudit], succeeds: false);
            Assert.IsTrue(File.Exists(conflictReport + ".failure.json"), "Conflict paths must be retained for diagnosis.");
            Assert.IsFalse(File.Exists(conflictReport), "A conflicting bundle must not produce a passing audit report.");
        }
        finally { Directory.Delete(root, recursive: true); }

        static string Q(string path) => MacValidation.CmakeLiteral(path);
        async Task<string> Run(string command, string[] arguments, bool succeeds = true)
        {
            var start = new ProcessStartInfo(command) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (string variable in new[] { "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH", "DYLD_FALLBACK_LIBRARY_PATH" })
                start.Environment.Remove(variable);
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                string output = await stdout + await stderr;
                TestContext.WriteLine(command + " " + string.Join(" ", arguments) + "\n" + output);
                if (succeeds) Assert.AreEqual(0, process.ExitCode, command + ": " + output);
                else Assert.AreNotEqual(0, process.ExitCode, "The broken native fixture was incorrectly accepted.");
                return output;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
            }
        }
    }
}
