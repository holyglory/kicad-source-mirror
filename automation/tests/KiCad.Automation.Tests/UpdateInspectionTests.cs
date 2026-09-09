using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateInspectionTests
{
    [TestMethod]
    public async Task MissingIncompleteBusyAndLegacyJournalsDoNotAcquireRecoveryAuthorityOrRewriteFiles()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux update inspection."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-inspection-").FullName;
        try
        {
            Guid operation = Guid.NewGuid();
            Assert.AreEqual("journal_missing", (await LinuxUpdateInspection.InspectAsync(root, operation)).Status);
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
            string journal = Directory.CreateDirectory(Path.Combine(root, "handovers", operation.ToString("D"))).FullName;
            Assert.AreEqual("journal_incomplete", (await LinuxUpdateInspection.InspectAsync(root, operation)).Status);
            string path = Path.Combine(journal, "operation.lock");
            await File.WriteAllBytesAsync(path, [12, 34]);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.AreEqual("operation_unavailable", (await LinuxUpdateInspection.InspectAsync(root, operation)).Status);
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            var legacy = await LinuxUpdateInspection.InspectAsync(root, operation);
            Assert.AreEqual("legacy_journal_unverifiable", legacy.Status);
            Assert.IsFalse(legacy.AutomaticRecoveryAvailable);
            CollectionAssert.AreEqual(new byte[] { 12, 34 }, await File.ReadAllBytesAsync(path));
            Assert.HasCount(1, Directory.GetFileSystemEntries(journal));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                LinuxUpdateInspection.InspectAsync(root, operation, cancelled.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task WrongOperationForeignEndpointDuplicateUnknownAndOversizedJournalAreRejectedWithoutChange()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux update inspection."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-inspection-input-").FullName;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            Guid operation = Guid.NewGuid();
            string journal = Directory.CreateDirectory(Path.Combine(root, "handovers", operation.ToString("D"))).FullName;
            await File.WriteAllBytesAsync(Path.Combine(journal, "operation.lock"), []);
            var request = new LinuxUpdateHandoffRequest(root, "selections/fixture", new string('a', 64), operation,
                LinuxProcessIdentity.Read(Environment.ProcessId), "", Guid.NewGuid(), "/tmp/inspection-fixture.sock", true);
            var previous = new InstalledLinuxUpdate(root, Path.Combine(root, "versions", new string('b', 64), "payload"),
                Path.Combine(root, "manager"), Path.Combine(root, "config.json"), new string('b', 64), new string('c', 40));
            var intent = new UpdateHandoffIntent(1, request, previous, DateTimeOffset.UtcNow);
            string intentPath = Path.Combine(journal, "intent.json");
            var state = new UpdateHandoffState("waiting_for_exit", journal);
            await File.WriteAllTextAsync(Path.Combine(journal, "state.json"), JsonSerializer.Serialize(state, json));
            await File.WriteAllTextAsync(Path.Combine(journal, "request.json"), JsonSerializer.Serialize(request, json));
            foreach (string invalid in new[]
            {
                JsonSerializer.Serialize(intent with { Request = request with { OperationId = Guid.NewGuid() } }, json),
                "{\"schemaVersion\":1,\"schemaVersion\":2}", new string('x', 65537)
            })
            {
                await File.WriteAllTextAsync(intentPath, invalid);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxUpdateInspection.InspectAsync(root, operation));
                Assert.AreEqual(invalid, await File.ReadAllTextAsync(intentPath));
            }
            await File.WriteAllTextAsync(intentPath, "{\"unexpected\":true}");
            await Assert.ThrowsExactlyAsync<JsonException>(() => LinuxUpdateInspection.InspectAsync(root, operation));
            await File.WriteAllTextAsync(intentPath, JsonSerializer.Serialize(intent, json));
            await File.WriteAllTextAsync(Path.Combine(journal, "state.json"), JsonSerializer.Serialize(state with
            { ProcessId = Environment.ProcessId, ProcessIdentity = request.OldProcess, Endpoint = "ipc:///tmp/foreign.sock" }, json));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxUpdateInspection.InspectAsync(root, operation));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InspectionCommandRejectsMalformedInvocationInsteadOfStartingMcp()
    {
        using var output = new StringWriter();
        Assert.AreEqual(1, await LinuxUpdateInspectionCommand.RunAsync(["--inspect-update"], output, CancellationToken.None));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.AreEqual("failed", result.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(result.RootElement.GetProperty("automaticRecoveryAvailable").GetBoolean());
    }
}
