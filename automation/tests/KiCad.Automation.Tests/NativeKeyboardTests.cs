using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeKeyboardTests
{
    [TestMethod]
    public void OnlyMissingWindowsDuringReadOnlyEnumerationAreRecoverable()
    {
        foreach (byte request in new byte[] { 3, 14, 15, 20 })
            Assert.IsTrue(NativeKeyboard.IsWindowEnumerationRace(3, request));
        foreach (byte error in new byte[] { 1, 2, 8, 9, 10 })
            Assert.IsFalse(NativeKeyboard.IsWindowEnumerationRace(error, 20));
        foreach (byte request in new byte[] { 4, 8, 12, 42, 132 })
            Assert.IsFalse(NativeKeyboard.IsWindowEnumerationRace(3, request));
    }
}
