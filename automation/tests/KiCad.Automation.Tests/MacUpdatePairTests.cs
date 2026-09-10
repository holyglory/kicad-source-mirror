using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

internal static class MacUpdatePair
{
    internal static void Validate(string baseline, string candidate)
    {
        static bool Commit(string value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigitLower);
        if (!Commit(baseline) || !Commit(candidate) || baseline == candidate)
            throw new InvalidDataException("The Mac update journey requires two different exact source commits.");
    }
}

[TestClass]
public sealed class MacUpdatePairTests
{
    [TestMethod]
    public void RequiresDifferentResolvedBuildsBeforeDownloadingOrLaunching()
    {
        MacUpdatePair.Validate(new string('a', 40), new string('b', 40));
        foreach (string invalid in new[] { "", "master", "abcdef", new string('A', 40), new string('g', 40) })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => MacUpdatePair.Validate(invalid, new string('b', 40)));
            Assert.ThrowsExactly<InvalidDataException>(() => MacUpdatePair.Validate(new string('a', 40), invalid));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => MacUpdatePair.Validate(new string('a', 40), new string('a', 40)));
    }
}
