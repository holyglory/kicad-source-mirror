using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class McpToolPayloadTests
{
    [TestMethod]
    public void ReadsBothNativePackageToolShapesWithoutTreatingErrorsAsSuccess()
    {
        foreach (var result in new[]
        {
            JsonSerializer.SerializeToElement(new { structuredContent = new { instanceId = "fixture" } }),
            JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "{\"instanceId\":\"fixture\"}" } } })
        }) Assert.AreEqual("fixture", McpToolPayload.Object(result).GetProperty("instanceId").GetString());
        foreach (string invalid in new[]
        {
            "{}", "{\"isError\":true,\"structuredContent\":{}}", "{\"structuredContent\":[]}",
            "{\"content\":[{\"type\":\"text\",\"text\":\"[]\"}]}",
            "{\"content\":[{\"type\":\"text\",\"text\":\"{}\"},{\"type\":\"text\",\"text\":\"{}\"}]}"
        })
        {
            using var parsed = JsonDocument.Parse(invalid);
            Assert.ThrowsExactly<InvalidDataException>(() => McpToolPayload.Object(parsed.RootElement));
        }
    }
}
