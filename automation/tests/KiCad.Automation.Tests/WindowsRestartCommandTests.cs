using System.Text;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsRestartCommandTests
{
    [TestMethod]
    public void WindowsProtocolPreservesCreationTimeAndRejectsDuplicatesAndWrongSchema()
    {
        string path = Path.GetFullPath("fixture");
        var request = new WindowsUpdateHandoffRequest(path, Guid.NewGuid().ToString("D"), new string('a', 64),
            Guid.NewGuid(), new(123, "134200000000000001", path), "", Guid.NewGuid(), path, false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new WindowsRestartConfiguration(3, request), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.AreEqual(request, WindowsRestartCommand.Parse(bytes).Request);
        foreach (string json in new[] { "{}", "{\"schemaVersion\":3,\"schemaVersion\":3}",
            Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":3", "\"schemaVersion\":2", StringComparison.Ordinal),
            Encoding.UTF8.GetString(bytes).Replace("\"processId\":123", "\"processId\":123,\"processId\":123", StringComparison.Ordinal) })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRestartCommand.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<JsonException>(() => WindowsRestartCommand.Parse("{"u8.ToArray()));
    }

    [TestMethod]
    public async Task InvalidOrCancelledCommandsNeverStartMcpOrAReplacement()
    {
        using var output = new StringWriter();
        Assert.AreEqual(1, await WindowsRestartCommand.RunAsync(["--restart-update"], output, default));
        using var failed = JsonDocument.Parse(output.ToString());
        Assert.AreEqual("failed", failed.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(failed.RootElement.GetProperty("nativeEditorRestarted").GetBoolean());
        output.GetStringBuilder().Clear();
        Assert.AreEqual(2, await WindowsRestartCommand.RunAsync([], output, new CancellationToken(true)));
        using var cancelled = JsonDocument.Parse(output.ToString());
        Assert.AreEqual("cancelled", cancelled.RootElement.GetProperty("status").GetString());
    }
}
