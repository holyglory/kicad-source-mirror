using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeKeyboardTests
{
    [TestMethod]
    public void RequestedLetterCaseAndPunctuationChooseTheActualKeymapLevel()
    {
        Assert.AreEqual((nuint)'A', NativeKeyboard.LiteralKeysym("A"));
        Assert.AreEqual((nuint)'_', NativeKeyboard.LiteralKeysym("_"));
        Assert.AreEqual((nuint)' ', NativeKeyboard.LiteralKeysym(" "));
        Assert.IsNull(NativeKeyboard.LiteralKeysym("Return"));
        Assert.IsNull(NativeKeyboard.LiteralKeysym(""));
        Assert.IsFalse(NativeKeyboard.RequiresShift('a', 'a', 'A'));
        Assert.IsTrue(NativeKeyboard.RequiresShift('A', 'a', 'A'));
        Assert.IsFalse(NativeKeyboard.RequiresShift('-', '-', '_'));
        Assert.IsTrue(NativeKeyboard.RequiresShift('_', '-', '_'));
        Assert.IsFalse(NativeKeyboard.RequiresShift(0xff0d, 0xff0d, 0xff0d)); // Return, not text case
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeKeyboard.RequiresShift('x', 'a', 'A'));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeKeyboard.RequiresShift(0, 0, 0));
    }

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
