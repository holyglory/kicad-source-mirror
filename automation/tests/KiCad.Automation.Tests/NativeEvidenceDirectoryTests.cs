using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeEvidenceDirectoryTests
{
    [TestMethod]
    public void NewRunPreservesPreviousEvidenceWithoutRetainingItAgain()
    {
        string root = Directory.CreateTempSubdirectory("kicad-evidence-test-").FullName;
        try
        {
            string first = NativeEvidenceDirectory.Begin(root);
            Directory.CreateDirectory(Path.Combine(first, "nested"));
            File.WriteAllText(Path.Combine(first, "nested", "screenshot-receipt"), "original bytes");
            string second = NativeEvidenceDirectory.Begin(root);
            Assert.AreEqual(first, second);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(second).ToArray());
            string archived = Directory.GetDirectories(Path.Combine(root, "native-session-history")).Single();
            Assert.AreEqual("original bytes", File.ReadAllText(Path.Combine(archived, "nested", "screenshot-receipt")));
            File.WriteAllText(Path.Combine(second, "new-receipt"), "new bytes");
            NativeEvidenceDirectory.Begin(root);
            Assert.AreEqual(2, Directory.GetDirectories(Path.Combine(root, "native-session-history")).Length);
            Assert.AreEqual("original bytes", File.ReadAllText(Path.Combine(archived, "nested", "screenshot-receipt")));
        }
        finally { Directory.Delete(root, true); } // Only this test's disposable directory.
    }
}
