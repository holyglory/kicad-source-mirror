using System.Runtime.InteropServices;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LinuxStagingTests
{
    [TestMethod]
    public void IncompleteInstallCannotPassTheStagingInventory()
    {
        string root = Directory.CreateTempSubdirectory("kicad-staging-fixture-").FullName;
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => LinuxStaging.VerifyTree(root));
            // Synthetic file-presence fixture only, not an executable package.
            foreach (string name in LinuxStaging.RequiredExecutables) Write("bin/" + name);
            foreach (string name in LinuxStaging.RequiredInterfaces) Write("bin/_" + name + ".kiface");
            foreach (string name in new[] { "libkicommon.so", "libkiapi.so" }) Write("lib/" + name);
            foreach (string name in new[] { "kicad-mcp", "kicad-mcp.dll", "libcoreclr.so", "libhostpolicy.so", "libnng.so" })
                Write("lib/kicad-automation/" + name);
            Directory.CreateDirectory(Path.Combine(root, "share", "kicad"));
            LinuxStaging.VerifyTree(root);
            string core = Path.Combine(root, "lib/kicad-automation/libcoreclr.so");
            File.WriteAllText(core, "");
            Assert.ThrowsExactly<InvalidDataException>(() => LinuxStaging.VerifyTree(root));
            Write("lib/kicad-automation/libcoreclr.so");
            File.Delete(Path.Combine(root, "bin/eeschema"));
            Assert.ThrowsExactly<InvalidDataException>(() => LinuxStaging.VerifyTree(root));
            Assert.IsFalse(new LinuxStageResult(1, "staged", root, null, []).QualifyingDelivery);

            void Write(string relative)
            {
                string path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "Synthetic inventory fixture; not an executable.");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InvalidAndCancelledRequestsDoNotCreateAnInstallTree()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        string root = Directory.CreateTempSubdirectory("kicad-staging-input-").FullName;
        try
        {
            var request = new LinuxStageRequest(root, root, Path.Combine(root, "libnng.so"), root);
            Assert.ThrowsExactly<ArgumentException>(() => LinuxStaging.ValidateInputs(request));
            foreach (string name in new[] { "cmake_install.cmake", "kicad-mcp", "kicad-mcp.dll", "libcoreclr.so", "libhostpolicy.so", "libnng.so" })
                File.WriteAllText(Path.Combine(root, name), "Synthetic input fixture.");
            LinuxStaging.ValidateInputs(request);
            Assert.ThrowsExactly<ArgumentException>(() => LinuxStaging.ValidateInputs(request with { OutputRoot = "/" }));
            Assert.ThrowsExactly<ArgumentException>(() => LinuxStaging.ValidateInputs(request with { NativeBuild = "relative" }));
            string[] before = Directory.GetFileSystemEntries(root).Order().ToArray();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                LinuxStaging.RunAsync(request, new CancellationToken(canceled: true)));
            CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(root).Order().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
}
