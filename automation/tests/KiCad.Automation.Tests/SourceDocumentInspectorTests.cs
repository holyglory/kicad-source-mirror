using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Mcp;
using ModelContextProtocol.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("NativeDocuments")]
public sealed class SourceDocumentInspectorTests
{
    [TestMethod]
    public async Task InvalidPageScaleIdentityAndDigestFailBeforeStartingNativeTools()
    {
        using var fixture = new PdfFixture();
        int starts = 0;
        var inspector = new SourceDocumentInspector { ProcessStarted = _ => starts++ };
        foreach (var (page, scale) in new[] { (0, 400), (-1, 400), (1, 0), (1, 8193) })
            Assert.AreEqual("invalid_document_request", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, page, CancellationToken.None, scale))).Code);
        foreach (string hash in new[] { "", "abcd", new string('z', 64) })
            Assert.AreEqual("invalid_document_request", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 1, CancellationToken.None, expectedSha256: hash))).Code);
        foreach (var document in new[] { fixture.Document with { Id = Guid.Empty }, fixture.Document with { Revision = " " } })
            Assert.AreEqual("invalid_document_request", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                inspector.InspectPdfPageAsync(document, fixture.Path, 1, CancellationToken.None))).Code);
        Assert.AreEqual(0, starts, "Invalid targeting must not launch a native tool.");
    }

    [TestMethod]
    public async Task ToolResolvesManifestIdentityAndReturnsImageAndProvenance()
    {
        using var fixture = new PdfFixture();
        var repository = new HardwareRepository(Guid.NewGuid(), "PDF test", [], [fixture.Document], [], []);
        string manifest = System.IO.Path.Combine(fixture.Directory, "hardware.xml");
        await File.WriteAllTextAsync(manifest, HardwareRepositoryXml.Write(repository));
        var tool = new DocumentTools();
        Assert.IsTrue((await tool.ReadPdfPage("hardware.xml", fixture.Document.Id.ToString("D"), 1,
            CancellationToken.None)).IsError == true, "The tool must not select a manifest from an implicit current directory.");
        var result = await tool.ReadPdfPage(manifest, fixture.Document.Id.ToString("D"), 1, CancellationToken.None, 400);
        Assert.IsFalse(result.IsError == true);
        Assert.AreEqual(2, result.Content.Count);
        Assert.IsInstanceOfType<ImageContentBlock>(result.Content[1]);
        Assert.AreEqual(fixture.Document.Id.ToString("D"), result.StructuredContent!.Value.GetProperty("documentId").GetString());
        Assert.AreEqual(1, result.StructuredContent.Value.GetProperty("page").GetInt32());
        StringAssert.Contains(result.StructuredContent.Value.GetProperty("text").GetString()!, "Operating voltage");
        var missing = await tool.ReadPdfPage(manifest, Guid.NewGuid().ToString("D"), 1, CancellationToken.None);
        Assert.IsTrue(missing.IsError == true);
        var changed = await tool.ReadPdfPage(manifest, fixture.Document.Id.ToString("D"), 1,
            CancellationToken.None, expectedSha256: new string('0', 64));
        Assert.IsTrue(changed.IsError == true);
        Assert.IsFalse((await tool.ReadPdfPage(manifest, fixture.Document.Id.ToString("D"), 3,
            CancellationToken.None, 200)).IsError == true);
    }

    [TestMethod]
    public async Task RealPdfPagesKeepTextImagesAndSourceIdentityTogether()
    {
        using var fixture = new PdfFixture();
        var inspector = new SourceDocumentInspector();
        var operating = await inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 1,
            CancellationToken.None, maximumImageDimension: 400);
        var absolute = await inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 2,
            CancellationToken.None, maximumImageDimension: 400, expectedSha256: operating.SourceSha256);
        Assert.AreEqual(fixture.Document.Id, operating.DocumentId);
        Assert.AreEqual("synthetic-revision-A", operating.DeclaredRevision);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Path))), operating.SourceSha256);
        Assert.AreEqual(operating.SourceSha256, absolute.SourceSha256);
        Assert.AreEqual(1, operating.Page); Assert.AreEqual(2, absolute.Page);
        StringAssert.Contains(operating.Text, "Operating voltage: 3.3 V");
        Assert.IsFalse(operating.Text.Contains("Absolute maximum", StringComparison.Ordinal));
        StringAssert.Contains(absolute.Text, "Absolute maximum voltage: 5 V");
        Assert.AreEqual(SourceTextAvailability.Extracted, operating.TextAvailability);
        Assert.IsTrue(operating.Png.Length > 100);
        Assert.IsFalse(operating.Png.SequenceEqual(absolute.Png));
        // A graphics-only page is still returned for visual inspection. No
        // text, OCR result, or electrical interpretation may be invented.
        var graphics = await inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 3,
            CancellationToken.None, maximumImageDimension: 400);
        Assert.AreEqual(SourceTextAvailability.NoExtractableText, graphics.TextAvailability);
        Assert.IsTrue(string.IsNullOrWhiteSpace(graphics.Text));
        Assert.IsTrue(graphics.Png.Length > 100);
        Assert.IsTrue(File.Exists(fixture.Path));
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        string evidence = System.IO.Path.Combine(root.FullName, "artifacts", "source-document-tests", "pages", fixture.Document.Id.ToString("D"));
        System.IO.Directory.CreateDirectory(evidence);
        foreach (var page in new[] { operating, absolute, graphics })
        {
            await File.WriteAllBytesAsync(System.IO.Path.Combine(evidence, $"page-{page.Page}.png"), page.Png);
            await File.WriteAllTextAsync(System.IO.Path.Combine(evidence, $"page-{page.Page}.txt"), page.Text);
        }
        await File.WriteAllBytesAsync(System.IO.Path.Combine(evidence, "synthetic-source.pdf"), await File.ReadAllBytesAsync(fixture.Path));
        await File.WriteAllTextAsync(System.IO.Path.Combine(evidence, "provenance.json"), JsonSerializer.Serialize(new
        {
            syntheticFixture = true, documentId = fixture.Document.Id, declaredRevision = fixture.Document.Revision,
            sourceSha256 = operating.SourceSha256,
            pages = new[] { operating, absolute, graphics }.Select(p => new
            { page = p.Page, textAvailability = p.TextAvailability.ToString(), imageSha256 = Convert.ToHexStringLower(SHA256.HashData(p.Png)) })
        }));
    }

    [TestMethod]
    public async Task MissingChangedAndInvalidSourcesFailWithoutReplacingTheDocument()
    {
        using var fixture = new PdfFixture();
        var inspector = new SourceDocumentInspector();
        byte[] original = await File.ReadAllBytesAsync(fixture.Path);
        Assert.AreEqual("source_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 1, CancellationToken.None,
                expectedSha256: new string('0', 64)))).Code);
        Assert.AreEqual("document_io", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            inspector.InspectPdfPageAsync(fixture.Document, fixture.Path + ".missing", 1, CancellationToken.None))).Code);
        Assert.AreEqual("document_tool_failed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 4, CancellationToken.None))).Code);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Path));
        await File.WriteAllTextAsync(fixture.Path, "This is deliberately not a PDF.");
        Assert.AreEqual("document_tool_failed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 1, CancellationToken.None))).Code);
        Assert.AreEqual("This is deliberately not a PDF.", await File.ReadAllTextAsync(fixture.Path));
        await File.WriteAllBytesAsync(fixture.Path, original);
        Assert.AreEqual(SourceTextAvailability.Extracted,
            (await inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 1, CancellationToken.None, 200)).TextAvailability);
    }

    [TestMethod]
    public async Task CancelledAndUnavailableToolsLeaveSourcesUsable()
    {
        using var fixture = new PdfFixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SourceDocumentInspector().InspectPdfPageAsync(fixture.Document, fixture.Path, 1, cancelled.Token));
        var missingTool = new SourceDocumentInspector(textExecutable: System.IO.Path.Combine(fixture.Directory, "no-such-tool"));
        Assert.AreEqual("document_tool_unavailable", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
            missingTool.InspectPdfPageAsync(fixture.Document, fixture.Path, 1, CancellationToken.None))).Code);
        Assert.AreEqual(SourceTextAvailability.Extracted,
            (await new SourceDocumentInspector().InspectPdfPageAsync(fixture.Document, fixture.Path, 1,
                CancellationToken.None, 200)).TextAvailability);
    }

    [TestMethod]
    public async Task CancellationAfterNativeProcessStartupIsObservedAndRecoveryWorks()
    {
        using var fixture = new PdfFixture();
        using var cancelled = new CancellationTokenSource();
        int? processId = null;
        var inspector = new SourceDocumentInspector
        {
            ProcessStarted = id => { processId = id; cancelled.Cancel(); }
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            inspector.InspectPdfPageAsync(fixture.Document, fixture.Path, 1, cancelled.Token));
        Assert.IsNotNull(processId, "Cancellation must occur after a real native tool was started.");
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
            Assert.IsTrue(process.HasExited, "The call must not leave its cancelled native process running.");
        }
        catch (ArgumentException) { /* Reaped by the inspector: no such process. */ }
        Assert.AreEqual(SourceTextAvailability.Extracted,
            (await new SourceDocumentInspector().InspectPdfPageAsync(fixture.Document, fixture.Path, 1,
                CancellationToken.None, 200)).TextAvailability);
    }

    // Compiled synthetic fixture generation, not a production datasheet or a
    // shell/script dependency. Explicit byte offsets make a valid real PDF.
    internal sealed class PdfFixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("kicad-pdf-test-").FullName;
        public string Path { get; }
        public HardwareDocument Document { get; }
        public PdfFixture()
        {
            Path = System.IO.Path.Combine(Directory, "synthetic source.pdf");
            Document = new(Guid.NewGuid(), "synthetic source.pdf", "synthetic-revision-A", "Isolated test evidence");
            string[] contents = ["BT /F1 14 Tf 20 170 Td (Operating voltage: 3.3 V) Tj ET\n",
                "BT /F1 14 Tf 20 170 Td (Absolute maximum voltage: 5 V) Tj ET\n",
                "0 0 1 rg 20 20 100 100 re f\n"];
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
            };
            for (int index = 0; index < contents.Length; index++)
            {
                objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + index * 2} 0 R >>");
                objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(contents[index])} >>\nstream\n{contents[index]}endstream");
            }
            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int> { 0 };
            for (int index = 0; index < objects.Count; index++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
                pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
            }
            int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
            pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
                .Append(xref).Append("\n%%EOF\n");
            File.WriteAllBytes(Path, Encoding.ASCII.GetBytes(pdf.ToString()));
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
