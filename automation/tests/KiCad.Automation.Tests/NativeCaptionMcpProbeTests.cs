using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeCaptionMcpProbeTests
{
    [TestMethod]
    [DataRow("native_status_4")]
    [DataRow("native_status_7")]
    public void NativeReadinessErrorsWorkInBothPublishedResponseShapes(string code)
    {
        var structured = JsonSerializer.SerializeToElement(new { isError = true, structuredContent = new { code } });
        var content = JsonSerializer.SerializeToElement(new { isError = true, content = new[]
        { new { type = "text", text = JsonSerializer.Serialize(new { code, message = "The canvas has no completed render to capture" }) } } });
        Assert.IsTrue(NativeCaptionMcpProbe.RenderPending(structured));
        Assert.IsTrue(NativeCaptionMcpProbe.RenderPending(content));
    }

    [TestMethod]
    [DataRow("{\"isError\":true,\"structuredContent\":{\"code\":\"native_status_3\"}}")]
    [DataRow("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"native_status_4 is mentioned but not a typed result\"}]}")]
    [DataRow("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"{}\"},{\"type\":\"text\",\"text\":\"{}\"}]}")]
    [DataRow("{\"isError\":false,\"structuredContent\":{\"code\":\"native_status_4\"}}")]
    [DataRow("{\"isError\":true,\"structuredContent\":{\"code\":4}}")]
    public void OtherErrorsAndAmbiguousContentDoNotRetry(string json)
    {
        using var result = JsonDocument.Parse(json);
        Assert.IsFalse(NativeCaptionMcpProbe.RenderPending(result.RootElement));
    }
}
