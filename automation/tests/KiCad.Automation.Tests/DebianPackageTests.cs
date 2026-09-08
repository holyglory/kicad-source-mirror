using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DebianPackageTests
{
    [TestMethod]
    public void InstalledPublicFilesRemainUsableByANonRootOperator()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("kicad-deb-mode-fixture-").FullName;
        try
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            string executable = Path.Combine(directory, "synthetic-launcher");
            string data = Path.Combine(directory, "synthetic-data");
            File.WriteAllText(executable, "Synthetic permission fixture.");
            File.WriteAllText(data, "Synthetic permission fixture.");
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute | UnixFileMode.SetGroup);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DebianPackage.NormalizePermissions(root);
            Assert.IsTrue(File.GetUnixFileMode(directory).HasFlag(UnixFileMode.OtherExecute));
            Assert.IsFalse(File.GetUnixFileMode(directory).HasFlag(UnixFileMode.SetGroup));
            Assert.IsTrue(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.OtherExecute));
            Assert.IsTrue(File.GetUnixFileMode(data).HasFlag(UnixFileMode.OtherRead));
            Assert.IsFalse(File.GetUnixFileMode(data).HasFlag(UnixFileMode.OtherExecute));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NativeAndDynamicallyLoadedDependenciesAreBothPreserved()
    {
        string dependencies = DebianPackage.CombineDependencies(
            "shlibs:Depends=libc6 (>= 2.38), libstdc++6 (>= 14), libglu1-mesa | libglu1\n");
        StringAssert.Contains(dependencies, "libc6 (>= 2.38)");
        StringAssert.Contains(dependencies, "libglu1-mesa | libglu1");
        StringAssert.Contains(dependencies, "libicu76");
        StringAssert.Contains(dependencies, "libssl3t64");
        StringAssert.Contains(dependencies, "poppler-utils");
        StringAssert.Contains(dependencies, "libngspice0");
        Assert.AreEqual(1, dependencies.Split(',').Count(d => d.Trim().StartsWith("libc6")));
    }

    [TestMethod]
    public void InvalidAnalysisCannotBecomePackageControlMetadata()
    {
        foreach (string invalid in new[] { "", "Depends=libc6", "shlibs:Depends=",
            "shlibs:Depends=libc6\nInjected: value", "shlibs:Depends=libc6; echo bad",
            "shlibs:Depends=libc6 |", "shlibs:Depends=libc6 (any version)" })
            Assert.ThrowsExactly<InvalidDataException>(() => DebianPackage.CombineDependencies(invalid));
    }
}
