using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateRegistrationRetryTests
{
    [TestMethod]
    public async Task ChangedInternalSnapshotIsRefreshedWithoutChangingTheRequestedCandidate()
    {
        string current = "initial"; var observed = new List<string>();
        string result = await UpdateRegistrationRetry.AgainstCurrentSelection(() => current, expected =>
        {
            observed.Add(expected);
            if (expected == "initial") { current = "selected-by-other-project"; throw new InvalidDataException("stale"); }
            return Task.FromResult("same-candidate-registered");
        });
        Assert.AreEqual("same-candidate-registered", result);
        CollectionAssert.AreEqual(new[] { "initial", "selected-by-other-project" }, observed.ToArray());
    }

    [TestMethod]
    public async Task CorruptionCancellationAndRepeatedChangesAreNotHidden()
    {
        int calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateRegistrationRetry.AgainstCurrentSelection<string>(() => "unchanged", _ =>
        { calls++; throw new InvalidDataException("wrong publisher"); }));
        Assert.AreEqual(1, calls);
        calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => UpdateRegistrationRetry.AgainstCurrentSelection<string>(() => "unchanged", _ =>
        { calls++; throw new OperationCanceledException(); }));
        Assert.AreEqual(1, calls);
        int generation = 0; calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdateRegistrationRetry.AgainstCurrentSelection<string>(() => generation.ToString(), _ =>
        { calls++; generation++; throw new InvalidDataException("changed again"); }));
        Assert.AreEqual(3, calls);
    }
}
