using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("NativeDocuments")]
public sealed class SourceDocumentStdioTests
{
    [TestMethod]
    public async Task RealStdioToolReturnsPdfImageWithExactSourceProvenanceAndRecoversFromBadTarget()
    {
        using var fixture = new SourceDocumentInspectorTests.PdfFixture();
        string manifest = Path.Combine(fixture.Directory, "hardware.xml");
        await File.WriteAllTextAsync(manifest, HardwareRepositoryXml.Write(new HardwareRepository(
            Guid.NewGuid(), "STDIO source fixture", [], [fixture.Document], [], [])));
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll"));
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = Path.Combine(fixture.Directory, "state");
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> diagnostics = process.StandardError.ReadToEndAsync();
        try
        {
            var initialized = await Request(1, "initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "compiled-source-page-fixture", version = "1" }
            });
            Assert.IsTrue(initialized.TryGetProperty("result", out _));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var listed = await Request(2, "tools/list", new { });
            Assert.IsTrue(listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Any(t => t.GetProperty("name").GetString() == "kicad_source_pdf_page"));
            var page = (await Request(3, "tools/call", new { name = "kicad_source_pdf_page", arguments = new
            { manifestPath = manifest, documentId = fixture.Document.Id.ToString("D"), page = 1, maximumImageDimension = 400 } })).GetProperty("result");
            Assert.IsFalse(page.TryGetProperty("isError", out var error) && error.GetBoolean());
            var content = page.GetProperty("content");
            var image = content.EnumerateArray().Single(c => c.GetProperty("type").GetString() == "image");
            Assert.AreEqual("image/png", image.GetProperty("mimeType").GetString());
            Assert.IsTrue(Convert.FromBase64String(image.GetProperty("data").GetString()!).Length > 100);
            var state = page.GetProperty("structuredContent");
            Assert.AreEqual(fixture.Document.Id.ToString("D"), state.GetProperty("documentId").GetString());
            Assert.AreEqual(1, state.GetProperty("page").GetInt32());
            StringAssert.Contains(state.GetProperty("text").GetString()!, "Operating voltage");
            string digest = state.GetProperty("sourceSha256").GetString()!;
            var bad = (await Request(4, "tools/call", new { name = "kicad_source_pdf_page", arguments = new
            { manifestPath = manifest, documentId = Guid.NewGuid().ToString("D"), page = 1 } })).GetProperty("result");
            Assert.IsTrue(bad.GetProperty("isError").GetBoolean());
            var recovered = (await Request(5, "tools/call", new { name = "kicad_source_pdf_page", arguments = new
            { manifestPath = manifest, documentId = fixture.Document.Id.ToString("D"), page = 3,
                maximumImageDimension = 200, expectedSha256 = digest } })).GetProperty("result");
            Assert.AreEqual("NoExtractableText", recovered.GetProperty("structuredContent").GetProperty("textAvailability").GetString());
            Assert.IsTrue(recovered.GetProperty("content").EnumerateArray().Any(c => c.GetProperty("type").GetString() == "image"));
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, process.ExitCode, await diagnostics);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await diagnostics;
        }

        async Task<JsonElement> Request(int id, string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                Assert.IsNotNull(line, "MCP terminated before replying.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    return response.RootElement.Clone();
            }
        }
    }
}
