using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum SourceTextAvailability { Extracted, NoExtractableText }
public sealed record SourcePageInspection(Guid DocumentId, string DeclaredRevision, string SourceSha256,
    int Page, string Text, SourceTextAvailability TextAvailability, byte[] Png,
    IReadOnlyList<string> Diagnostics);

/// <summary>Reads one exact PDF page as evidence. Text is never interpreted as
/// an engineering value or instruction. No OCR, symbol creation, or claim that
/// a blank extraction means a blank page. SA-02/05: local native tools and source
/// documents; existing OS permissions and PDF restrictions remain in force.</summary>
public sealed class SourceDocumentInspector(string textExecutable = "pdftotext", string renderExecutable = "pdftoppm")
{
    // Test instrumentation at the real process boundary, not a substitute tool.
    internal Action<int>? ProcessStarted { get; init; }

    public async Task<SourcePageInspection> InspectPdfPageAsync(HardwareDocument document,
        string sourcePath, int page, CancellationToken token, int maximumImageDimension = 1800,
        string? expectedSha256 = null)
    {
        if (document.Id == Guid.Empty || string.IsNullOrWhiteSpace(document.Revision)
            || page < 1 || maximumImageDimension < 1 || maximumImageDimension > 8192)
            throw Failure("invalid_document_request", "Specify a document identity, declared revision, positive page and image dimension from 1 through 8192.");
        if (expectedSha256 is not null && (expectedSha256.Length != 64
            || expectedSha256.Any(c => !char.IsAsciiHexDigit(c))))
            throw Failure("invalid_document_request", "Expected source hash must be a SHA-256 hexadecimal value.");
        token.ThrowIfCancellationRequested();
        string staging = Directory.CreateTempSubdirectory("kicad-source-page-").FullName;
        bool failed = false;
        try
        {
            // Both tools read the same captured bytes, even if the repository
            // file changes between extraction and rendering. The digest identifies
            // those bytes, not an inferred publisher revision.
            string snapshot = Path.Combine(staging, "source.pdf");
            await using (var input = new FileStream(Path.GetFullPath(sourcePath), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, token);
            string hash;
            await using (var input = File.OpenRead(snapshot))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
            if (expectedSha256 is not null && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw Failure("source_changed", "Source bytes do not match the requested document revision hash; reselect the source.");

            string number = page.ToString(CultureInfo.InvariantCulture);
            var text = await RunAsync(textExecutable,
                ["-f", number, "-l", number, "-layout", "-enc", "UTF-8", "-eol", "unix", "-nopgbrk", snapshot, "-"],
                8 * 1024 * 1024, token);
            string imagePrefix = Path.Combine(staging, "page");
            var image = await RunAsync(renderExecutable,
                ["-f", number, "-l", number, "-singlefile", "-scale-to",
                    maximumImageDimension.ToString(CultureInfo.InvariantCulture), "-png", snapshot, imagePrefix],
                65536, token);
            string imagePath = imagePrefix + ".png";
            if (!File.Exists(imagePath))
                throw Failure("document_render_failed", "The requested page did not produce an image.");
            if (new FileInfo(imagePath).Length > 64 * 1024 * 1024)
                throw Failure("document_output_limit", "Rendered page exceeds the supported image size; request a smaller image dimension.");
            byte[] png = await File.ReadAllBytesAsync(imagePath, token);
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw Failure("document_render_failed", "The native renderer did not return a PNG page.");
            string extracted = new UTF8Encoding(false, true).GetString(text.Output);
            return new(document.Id, document.Revision, hash, page, extracted,
                string.IsNullOrWhiteSpace(extracted) ? SourceTextAvailability.NoExtractableText : SourceTextAvailability.Extracted,
                png, new[] { text.Diagnostic, image.Diagnostic }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
        }
        catch (Exception error)
        {
            failed = true;
            if (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
                throw Failure("document_io", error.Message);
            throw;
        }
        finally
        {
            // This directory contains only this call's disposable source copy
            // and output. Never remove or rewrite the caller's document.
            try { Directory.Delete(staging, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (!failed) throw Failure("document_cleanup_failed", "Could not remove the disposable document workspace: " + error.Message);
                // Keep the original failure/cancellation, while reporting that
                // this disposable workspace could not be reclaimed.
                Console.Error.WriteLine("Document workspace cleanup failed: " + error.Message);
            }
        }
    }

    private sealed record NativeOutput(byte[] Output, string Diagnostic);

    private async Task<NativeOutput> RunAsync(string executable, string[] arguments,
        int outputLimit, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw Failure("document_tool_unavailable", "Could not start the configured native document tool.");
        }
        catch (Win32Exception error)
        { throw Failure("document_tool_unavailable", error.Message); }

        var output = ReadBounded(process.StandardOutput.BaseStream, outputLimit);
        var diagnostic = ReadBounded(process.StandardError.BaseStream, 65536);
        try
        {
            ProcessStarted?.Invoke(process.Id);
            await Task.WhenAll(output, diagnostic, process.WaitForExitAsync(deadline.Token));
            token.ThrowIfCancellationRequested();
            string detail = Encoding.UTF8.GetString(await diagnostic);
            if (process.ExitCode != 0)
                throw Failure(process.ExitCode == 3 ? "document_permissions" : "document_tool_failed",
                    $"Native document tool exited with code {process.ExitCode}: {detail}");
            return new(await output, detail);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Failure("document_tool_timeout", "The native document tool exceeded its execution deadline."); }
        finally
        {
            await deadline.CancelAsync();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // Observe both readers even if startup instrumentation or waiting
            // failed before Task.WhenAll. Preserve the primary failure above.
            try { await Task.WhenAll(output, diagnostic); }
            catch (Exception) { }
        }

        async Task<byte[]> ReadBounded(Stream stream, int limit)
        {
            try
            {
                using var bytes = new MemoryStream();
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    if (bytes.Length + count > limit)
                        throw Failure("document_output_limit", "Native document output exceeds the supported page size.");
                    bytes.Write(buffer, 0, count);
                }
                return bytes.ToArray();
            }
            catch
            {
                await deadline.CancelAsync();
                throw;
            }
        }
    }

    private static AutomationException Failure(string code, string message) => new(code, message);
}
