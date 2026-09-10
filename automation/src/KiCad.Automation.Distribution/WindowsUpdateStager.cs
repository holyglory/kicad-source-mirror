using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

public sealed class StagedWindowsUpdate
{
    public string Directory { get; }
    public string ManifestSha256 { get; }
    public string Platform => "win-x64";
    internal StagedWindowsUpdate(string directory, string digest) { Directory = directory; ManifestSha256 = digest; }
}

/// <summary>Extract and inspect an authenticated Windows candidate in a new tree.
/// Never selects an installation, changes trust, closes an editor or restarts it.</summary>
public static class WindowsUpdateStager
{
    public static async Task<StagedWindowsUpdate> StageAsync(VerifiedUpdateManifest manifest, DownloadedUpdate download,
        string stagingRoot, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Windows x64 staging requires native Windows x64 execution.");
        var artifact = manifest.ForInstallation("win-x64", "zip");
        if (artifact is null || artifact != download.Artifact || download.ManifestSha256 != manifest.PayloadSha256)
            throw new InvalidDataException("Downloaded bytes do not belong to the signed Windows installation target.");
        if (!Path.IsPathFullyQualified(stagingRoot) || !System.IO.Directory.Exists(stagingRoot)
            || new DirectoryInfo(stagingRoot).LinkTarget is not null)
            throw new ArgumentException("Use an existing ordinary Windows update staging directory.");
        token.ThrowIfCancellationRequested();
        string work = Path.Combine(stagingRoot, "windows-stage-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(work);
        string payload = Path.Combine(work, "payload");
        try
        {
            WindowsArchivePlan plan;
            await using (var input = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var sealedArchive = new FileStream(Path.Combine(work, "verified.zip"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (input.Length != artifact.Bytes) throw new InvalidDataException("Windows download size changed before staging.");
                await CopyVerified(input, sealedArchive, artifact.Bytes, artifact.Sha256, token);
                sealedArchive.Flush(flushToDisk: true);
                sealedArchive.Position = 0;
                // Keep the sealed file exclusively open through inspection and extraction.
                plan = await ExtractAsync(sealedArchive, payload, token);
            }
            string bin = Path.Combine(payload, "bin");
            foreach (string name in new[] { "kicad.exe", "kicad-cli.exe", "kicad-mcp.exe", "coreclr.dll", "nng.dll" })
                RequireX64(Path.Combine(bin, name));
            string commit = (await Run("native-commit", Path.Combine(bin, "kicad-cli.exe"), ["version", "--format", "commit"])).Trim();
            if (commit != manifest.Release.Commit) throw new InvalidDataException("Staged Windows KiCad reports a different source commit.");
            string runtime = await Run("managed-runtime", Path.Combine(bin, "kicad-mcp.exe"), ["--runtime-info"]);
            RequireRuntime(runtime);
            await File.WriteAllTextAsync(Path.Combine(work, "staging.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "archive_staged", installationReady = false, platform = "win-x64",
                manifestSha256 = manifest.PayloadSha256, commit, manifest.Release.Version,
                archiveSha256 = artifact.Sha256, entries = plan.Entries.Count, plan.ExpandedBytes,
                nativeRuntimeVerified = true, authenticodeVerified = false, nativeEditorContacted = false
            }), token);
            return new(payload, manifest.PayloadSha256);
        }
        catch (Exception error)
        {
            string diagnostics = Path.Combine(stagingRoot, "windows-update-diagnostics-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(diagnostics);
            foreach (string log in System.IO.Directory.GetFiles(work, "*.log"))
                File.Move(log, Path.Combine(diagnostics, Path.GetFileName(log)));
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "failure.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = error is OperationCanceledException ? "cancelled" : "failed",
                installationReady = false, platform = "win-x64", manifestSha256 = manifest.PayloadSha256,
                error = new { kind = error.GetType().Name, message = error.Message }
            }), CancellationToken.None);
            try { System.IO.Directory.Delete(work, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException("Windows staging failed; its private partial tree remains at " + work, new AggregateException(error, cleanup)); }
            if (error is OperationCanceledException) throw;
            throw new IOException("Windows staging failed; retained diagnostics: " + diagnostics, error);
        }

        async Task<string> Run(string name, string executable, string[] arguments)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var start = new ProcessStartInfo(executable) { WorkingDirectory = work, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (string variable in new[] { "KICAD_AUTOMATION_NNG_LIBRARY", "KICAD_RUN_FROM_BUILD_DIR", "APPDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
                start.Environment.Remove(variable);
            // The entry points must use their packaged DLLs and Windows, not a build toolchain's PATH.
            start.Environment["PATH"] = Environment.SystemDirectory;
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(work, "configuration");
            start.Environment["KICAD_CACHE_HOME"] = Path.Combine(work, "cache");
            using var process = Process.Start(start) ?? throw new IOException("Could not start Windows staging check " + name);
            Task<string> stdout = Capture(process.StandardOutput, Path.Combine(work, name + ".stdout.log"));
            Task<string> stderr = Capture(process.StandardError, Path.Combine(work, name + ".stderr.log"));
            Task exited = process.WaitForExitAsync(deadline.Token);
            try
            {
                while (!exited.IsCompleted || !stdout.IsCompleted || !stderr.IsCompleted)
                {
                    Task[] pending = new Task[] { exited, stdout, stderr }.Where(task => !task.IsCompleted).ToArray();
                    if (pending.Length != 0) await Task.WhenAny(pending);
                    if (stdout.IsFaulted || stdout.IsCanceled) await stdout;
                    if (stderr.IsFaulted || stderr.IsCanceled) await stderr;
                    if (exited.IsFaulted || exited.IsCanceled) await exited;
                }
                await Task.WhenAll(exited, stdout, stderr);
                if (process.ExitCode != 0) throw new IOException("Windows staging " + name + " exited " + process.ExitCode + ".");
                return await stdout;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
            }

            async Task<string> Capture(StreamReader reader, string path)
            {
                await using var output = new StreamWriter(path, append: false, new UTF8Encoding(false));
                var text = new StringBuilder(); char[] buffer = new char[4096];
                int count;
                while ((count = await reader.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    int retained = Math.Min(count, 65536 - text.Length);
                    if (retained > 0) { text.Append(buffer, 0, retained); await output.WriteAsync(buffer.AsMemory(0, retained), CancellationToken.None); }
                    if (retained != count) throw new InvalidDataException("Windows staging subprocess output exceeded its limit.");
                }
                return text.ToString();
            }
        }
    }

    internal static async Task<WindowsArchivePlan> ExtractAsync(Stream archive, string destination, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destination) || System.IO.Directory.Exists(destination) || File.Exists(destination))
            throw new ArgumentException("Extract only to a new absolute Windows candidate directory.");
        var plan = await WindowsArchivePreflight.InspectAsync(archive, token);
        token.ThrowIfCancellationRequested();
        System.IO.Directory.CreateDirectory(destination);
        try
        {
            archive.Position = 0;
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var item in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (item.Directory) { EnsureDirectory(destination, item.Path); continue; }
                int slash = item.Path.LastIndexOf('/');
                string parent = slash < 0 ? destination : EnsureDirectory(destination, item.Path[..slash]);
                string target = Path.Combine(parent, item.Path[(slash + 1)..]);
                var entry = zip.GetEntry(item.Path) ?? throw new InvalidDataException("The Windows archive changed after inspection.");
                await using (var input = entry.Open())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await CopyVerified(input, output, item.Bytes, item.Sha256!, token);
                    output.Flush(flushToDisk: true);
                }
                RequireOrdinary(target, directory: false);
            }
            return plan;
        }
        catch
        {
            System.IO.Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static string EnsureDirectory(string root, string relative)
    {
        string parent = root;
        foreach (string component in relative.Split('/'))
        {
            string path = Path.Combine(parent, component);
            if (!System.IO.Directory.Exists(path)) System.IO.Directory.CreateDirectory(path);
            // Do not silently follow an 8.3 alias created for a different long name.
            if (!System.IO.Directory.EnumerateFileSystemEntries(parent).Any(entry =>
                string.Equals(Path.GetFileName(entry), component, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A Windows archive directory resolves through an undeclared alias.");
            RequireOrdinary(path, directory: true); parent = path;
        }
        return parent;
    }

    private static void RequireOrdinary(string path, bool directory)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new InvalidDataException("Extracted Windows update entry is not an ordinary file or directory.");
    }

    private static async Task CopyVerified(Stream input, Stream output, long length, string expectedHash, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024]; long copied = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (count > length - copied) throw new InvalidDataException("Windows update data grew during copying.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            hash.AppendData(buffer, 0, count); copied += count;
        }
        if (copied != length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash)
            throw new InvalidDataException("Windows update data no longer matches its authenticated size or hash.");
        await output.FlushAsync(token);
    }

    internal static void RequireX64(string path)
    {
        using var input = File.OpenRead(path);
        using var image = new PEReader(input);
        if (image.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            throw new InvalidDataException("Staged Windows binary is not x64: " + Path.GetFileName(path));
    }

    internal static void RequireRuntime(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement;
        if (result.GetProperty("schemaVersion").GetInt32() != 1 || result.GetProperty("status").GetString() != "runtime_available"
            || result.GetProperty("processArchitecture").GetString() != "X64"
            || !(result.GetProperty("framework").GetString() ?? "").StartsWith(".NET 10.", StringComparison.Ordinal)
            || result.GetProperty("nativeEditorContacted").GetBoolean() || result.GetProperty("crossPlatformReady").GetBoolean()
            || string.IsNullOrWhiteSpace(result.GetProperty("nngVersion").GetString()))
            throw new InvalidDataException("Staged Windows MCP did not verify its matching native transport.");
    }
}
