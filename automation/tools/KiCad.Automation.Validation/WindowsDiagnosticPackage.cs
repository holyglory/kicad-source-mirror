using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Validation;

/// <summary>Retain successfully built bytes after later qualification failure.
/// This is never a publishable package or a successful editor receipt.</summary>
public static class WindowsDiagnosticPackage
{
    public static async Task<bool> RetainAsync(string output, string commit,
        IReadOnlyList<ValidationStep> steps, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(output) || commit.Length != 40 || !commit.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Use the exact candidate output and source commit.");
        string[] prerequisites = ["native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime"];
        if (prerequisites.Any(name => !steps.Any(step => step.Name == name && step.ExitCode == 0))) return false;
        string install = Path.Combine(output, "install");
        foreach (string name in new[] { "kicad.exe", "kicad-cli.exe", "kicad-mcp.exe", "nng.dll" })
            if (!File.Exists(Path.Combine(install, "bin", name))) return false;
        token.ThrowIfCancellationRequested();
        string directory = Path.Combine(output, "diagnostics"); Directory.CreateDirectory(directory);
        string fileName = "unqualified-windows-" + commit + ".zip";
        string archive = Path.Combine(directory, fileName), partial = archive + ".partial";
        if (File.Exists(archive) || File.Exists(partial)) throw new IOException("Preserve the existing diagnostic artifact.");
        bool created = false;
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                int entries = 0; long bytes = 0;
                await Visit(new DirectoryInfo(install));
                async Task Visit(DirectoryInfo folder)
                {
                    token.ThrowIfCancellationRequested();
                    if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Diagnostic payload directories cannot be redirected.");
                    foreach (var item in folder.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++entries > 200000 || item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Invalid diagnostic payload entry.");
                        string relative = Path.GetRelativePath(install, item.FullName).Replace('\\', '/');
                        if (item is DirectoryInfo child) { zip.CreateEntry(relative + "/"); await Visit(child); }
                        else if (item is FileInfo file)
                        {
                            long length = file.Length; DateTime modified = file.LastWriteTimeUtc;
                            bytes = checked(bytes + length);
                            if (bytes > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("Diagnostic payload exceeds the package size ceiling.");
                            var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                            await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                            await using (var target = entry.Open()) await input.CopyToAsync(target, token);
                            file.Refresh();
                            if (file.Length != length || file.LastWriteTimeUtc != modified) throw new InvalidDataException("Diagnostic payload changed during capture.");
                        }
                        else throw new InvalidDataException("Unsupported diagnostic payload entry.");
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            string hash;
            await using (var stream = File.OpenRead(partial)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            File.Move(partial, archive);
            await File.WriteAllTextAsync(Path.Combine(directory, "windows-diagnostic.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Status = "unqualified_diagnostic", SourceCommit = commit,
                Archive = fileName, Bytes = new FileInfo(archive).Length, Sha256 = hash,
                Prerequisites = prerequisites, NativeEditorJourneyPassed = false, PubliclyPublishable = false
            }, Evidence.JsonOptions), token);
            return true;
        }
        finally { if (created && File.Exists(partial)) File.Delete(partial); }
    }
}
