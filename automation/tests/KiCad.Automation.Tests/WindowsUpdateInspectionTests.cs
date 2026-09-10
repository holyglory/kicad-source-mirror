using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsUpdateInspectionTests
{
    [TestMethod]
    public async Task InvalidAndCancelledCommandsReturnNonMutatingResults()
    {
        using var output = new StringWriter();
        Assert.AreEqual(1, await WindowsUpdateInspectionCommand.RunAsync(["--inspect-update"], output, default));
        using (var result = JsonDocument.Parse(output.ToString()))
            Assert.IsFalse(result.RootElement.GetProperty("automaticRecoveryAvailable").GetBoolean());
        output.GetStringBuilder().Clear();
        Assert.AreEqual(1, await WindowsUpdateRecoveryCommand.RunAsync(["--recover-update"], output, default));
        using (var result = JsonDocument.Parse(output.ToString()))
            Assert.IsFalse(result.RootElement.GetProperty("nativeEditorRestarted").GetBoolean());
        output.GetStringBuilder().Clear();
        Assert.AreEqual(2, await WindowsUpdateRecoveryCommand.RunAsync([], output, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task NativeMissingJournalInspectionAndRecoveryCreateNothing()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows-only observation."); return; }
        string root = Directory.CreateTempSubdirectory("kwinspect-").FullName;
        try
        {
            var id = Guid.NewGuid();
            Assert.AreEqual("journal_missing", (await WindowsUpdateInspection.InspectAsync(root, id)).Status);
            Assert.AreEqual("journal_missing", (await WindowsUpdateRecovery.RecoverAsync(root, id, Guid.NewGuid())).Status);
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }
}
