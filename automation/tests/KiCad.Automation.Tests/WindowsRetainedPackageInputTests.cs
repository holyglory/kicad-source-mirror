using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsRetainedPackageInputTests
{
    private const string Commit = "433c5e1fdf5954955a19df546236dce78e78b5b0";
    private const string Hash = "cd5381d6ebe716c7a63990a0bf3930fec0200a66f6f284e0433f1ea1e8c6dcc6";

    [TestMethod]
    public void ExactFrozenBuildInputDeterminesOnlyItsArtifactAndArchive()
    {
        var input = WindowsRetainedPackageInput.Parse($"34555975985/1/{Commit}/{Hash}");
        Assert.AreEqual("34555975985", input.RunId);
        Assert.AreEqual("1", input.Attempt);
        Assert.AreEqual(Commit, input.Commit);
        Assert.AreEqual(Hash, input.Sha256);
        Assert.AreEqual($"native-windows-x64-{Commit}-1", input.ArtifactName);
        Assert.AreEqual($"unqualified-windows-{Commit}.zip", input.ArchiveName);
    }

    [TestMethod]
    public void AmbiguousOrShellLikeBuildInputsAreRejected()
    {
        string valid = $"34555975985/1/{Commit}/{Hash}";
        foreach (string input in new[] { "", valid + "\n", valid + "/extra", " " + valid,
            valid.Replace("/1/", "/0/"), valid.Replace("/1/", "/01/"),
            valid.Replace(Commit, "HEAD"), valid.Replace(Commit, Commit.ToUpperInvariant()),
            valid.Replace(Hash, Hash[..63]), valid.Replace("34555975985", "../elsewhere"),
            valid.Replace("34555975985", "1;Invoke-Expression"), new string('a', 1000) })
            Assert.ThrowsExactly<ArgumentException>(() => WindowsRetainedPackageInput.Parse(input), input);
    }
}
