using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

public sealed class StagedMacUpdate
{
    public string Directory { get; }
    public string ManifestSha256 { get; }
    public string Platform { get; }
    internal StagedMacUpdate(string directory, string digest, string platform)
    { Directory = directory; ManifestSha256 = digest; Platform = platform; }
}

/// <summary>Preserve an authenticated Mac bundle in a new private tree, verify it,
/// and leave existing installations and processes untouched. Not activation.</summary>
public static class MacUpdateStager
{
    public static async Task<StagedMacUpdate> StageAsync(VerifiedUpdateManifest manifest, DownloadedUpdate download,
        string stagingRoot, CancellationToken token = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac update staging requires native macOS.");
        string platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64", Architecture.X64 => "osx-x64",
            _ => throw new PlatformNotSupportedException("Unsupported Mac update architecture.")
        };
        var artifact = manifest.ForInstallation(platform, "tar.gz");
        if (artifact is null || artifact != download.Artifact || download.ManifestSha256 != manifest.PayloadSha256)
            throw new InvalidDataException("Downloaded Mac bytes do not belong to this signed installation target.");
        if (!Path.IsPathFullyQualified(stagingRoot) || !System.IO.Directory.Exists(stagingRoot)
            || new DirectoryInfo(stagingRoot).LinkTarget is not null)
            throw new ArgumentException("Use an existing ordinary Mac update staging directory.");
        token.ThrowIfCancellationRequested();
        string work = Path.Combine(stagingRoot, "mac-stage-" + Guid.NewGuid().ToString("N"));
        if (System.IO.Directory.Exists(work) || File.Exists(work)) throw new IOException("The Mac staging destination already exists.");
        System.IO.Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string payload = Path.Combine(work, "payload");
        try
        {
            string sealedArchive = Path.Combine(work, "verified.tar.gz");
            await using (var input = File.OpenRead(download.Path))
            await using (var copy = new FileStream(sealedArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (input.Length != artifact.Bytes) throw new InvalidDataException("Mac download size changed before staging.");
                byte[] buffer = new byte[128 * 1024];
                int count;
                long copied = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    if (count > artifact.Bytes - copied) throw new InvalidDataException("Mac download grew during staging.");
                    await copy.WriteAsync(buffer.AsMemory(0, count), token);
                    hash.AppendData(buffer, 0, count);
                    copied += count;
                }
                if (copied != artifact.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != artifact.Sha256)
                    throw new InvalidDataException("Mac archive no longer matches its signed size and checksum.");
                await copy.FlushAsync(token);
                copy.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(sealedArchive, UnixFileMode.UserRead);
            MacArchivePlan plan;
            await using (var input = File.OpenRead(sealedArchive)) plan = await MacArchivePreflight.InspectAsync(input, token);
            System.IO.Directory.CreateDirectory(payload);
            await Run("extract", "/usr/bin/tar", ["-xpf", sealedArchive, "-C", payload,
                "--no-same-owner", "--mac-metadata", "--xattrs"]);
            await VerifyExtractedAsync(payload, plan, token);
            string bundle = Path.Combine(payload, "install/KiCad.app");
            await Run("native-signatures", "/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle]);
            await Run("managed-signature", "/usr/bin/codesign", ["--verify", "--strict", Path.Combine(payload, "managed/kicad-mcp")]);
            string architecture = platform == "osx-arm64" ? "arm64" : "x86_64";
            foreach (string binary in new[] { "install/KiCad.app/Contents/MacOS/kicad", "install/KiCad.app/Contents/MacOS/kicad-cli",
                "managed/kicad-mcp", "managed/libcoreclr.dylib", "managed/libnng.dylib" })
            {
                string architectures = await Run("architecture-" + Path.GetFileName(binary), "/usr/bin/lipo", ["-archs", Path.Combine(payload, binary)]);
                if (!architectures.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(architecture, StringComparer.Ordinal))
                    throw new InvalidDataException("Staged Mac binary lacks the installation architecture: " + binary);
            }
            string commit = (await Run("native-commit", Path.Combine(bundle, "Contents/MacOS/kicad-cli"), ["version", "--format", "commit"])).Trim();
            if (commit != manifest.Release.Commit) throw new InvalidDataException("Staged Mac KiCad reports a different source commit.");
            string probe = await Run("managed-runtime", Path.Combine(payload, "kicad-mcp"), ["--runtime-info"]);
            using (var json = JsonDocument.Parse(probe))
            {
                var info = json.RootElement;
                if (info.GetProperty("schemaVersion").GetInt32() != 1 || info.GetProperty("status").GetString() != "runtime_available"
                    || info.GetProperty("nativeEditorContacted").GetBoolean()
                    || info.GetProperty("crossPlatformReady").GetBoolean()
                    || info.GetProperty("processArchitecture").GetString() != RuntimeInformation.ProcessArchitecture.ToString()
                    || !(info.GetProperty("framework").GetString() ?? "").StartsWith(".NET 10.", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(info.GetProperty("nngVersion").GetString()))
                    throw new InvalidDataException("The staged Mac MCP runtime did not verify its matching native transport.");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "staging.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "archive_staged", installationReady = false, platform,
                manifestSha256 = manifest.PayloadSha256, commit, manifest.Release.Version,
                archiveSha256 = artifact.Sha256, entries = plan.Entries.Count, plan.ExpandedBytes,
                plan.ExtendedAttributeEntries, appleDoubleEntries = plan.Entries.Count(x => x.AppleDouble),
                nativeSignaturesVerified = true, nativeRuntimeVerified = true
            }), token);
            return new(payload, manifest.PayloadSha256, platform);
        }
        catch (Exception error)
        {
            string diagnostics = Path.Combine(stagingRoot, "mac-update-diagnostics-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(diagnostics);
            foreach (string log in System.IO.Directory.GetFiles(work, "*.log"))
                File.Move(log, Path.Combine(diagnostics, Path.GetFileName(log)));
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "failure.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = error is OperationCanceledException ? "cancelled" : "failed",
                installationReady = false, platform, manifestSha256 = manifest.PayloadSha256,
                error = new { kind = error.GetType().Name, message = error.Message }
            }), CancellationToken.None);
            try { RemoveOwnedTree(work); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException("Mac staging failed; its private partial tree could not be removed: " + work, new AggregateException(error, cleanup)); }
            if (error is OperationCanceledException) throw;
            throw new IOException("Mac update staging failed; retained diagnostics: " + diagnostics, error);
        }

        async Task<string> Run(string name, string executable, string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable) { WorkingDirectory = work, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (string variable in new[] { "COPYFILE_DISABLE", "TAR_OPTIONS", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH",
                "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_FALLBACK_FRAMEWORK_PATH", "KICAD_RUN_FROM_BUILD_DIR", "APPDIR", "KICAD_AUTOMATION_NNG_LIBRARY" })
                start.Environment.Remove(variable);
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(work, "configuration");
            start.Environment["KICAD_CACHE_HOME"] = Path.Combine(work, "cache");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            using var process = Process.Start(start) ?? throw new IOException("Could not start Mac staging check " + name);
            Task<string> stdout = ReadBounded(process.StandardOutput, deadline.Token);
            Task<string> stderr = ReadBounded(process.StandardError, deadline.Token);
            Task exited = process.WaitForExitAsync(deadline.Token);
            try
            {
                while (!exited.IsCompleted || !stdout.IsCompleted || !stderr.IsCompleted)
                {
                    Task[] pending = new Task[] { exited, stdout, stderr }.Where(x => !x.IsCompleted).ToArray();
                    if (pending.Length != 0) await Task.WhenAny(pending);
                    if (stdout.IsFaulted || stdout.IsCanceled) await stdout;
                    if (stderr.IsFaulted || stderr.IsCanceled) await stderr;
                    if (exited.IsFaulted || exited.IsCanceled) await exited;
                }
                await Task.WhenAll(exited, stdout, stderr);
                await File.WriteAllTextAsync(Path.Combine(work, name + ".stdout.log"), await stdout, CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(work, name + ".stderr.log"), await stderr, CancellationToken.None);
                if (process.ExitCode != 0)
                    throw new IOException("Mac staging " + name + " exited " + process.ExitCode + ": " + (await stderr)[..Math.Min(512, (await stderr).Length)]);
                return await stdout;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
            }
        }
    }

    internal static async Task VerifyExtractedAsync(string payload, MacArchivePlan plan, CancellationToken token)
    {
        var expected = plan.Entries.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in plan.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(payload, entry.Path);
            for (string? parent = Path.GetDirectoryName(entry.Path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                directories.Add(parent);
            if (entry.AppleDouble)
            {
                if (File.Exists(path) || System.IO.Directory.Exists(path)) throw new InvalidDataException("AppleDouble metadata was not consumed by the native extractor.");
                continue;
            }
            if (entry.Type == System.Formats.Tar.TarEntryType.SymbolicLink)
            {
                if (new FileInfo(path).LinkTarget != entry.ArchivedLinkTarget)
                    throw new InvalidDataException("Extracted Mac symbolic link changed: " + entry.Path);
                continue;
            }
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) throw new InvalidDataException("Unexpected link in extracted Mac payload.");
            if (entry.Type == System.Formats.Tar.TarEntryType.Directory)
            {
                if (!System.IO.Directory.Exists(path)) throw new InvalidDataException("Missing Mac package directory.");
                directories.Add(entry.Path);
            }
            else
            {
                if (!info.Exists || info.Length != entry.Bytes) throw new InvalidDataException("Missing or changed extracted Mac file: " + entry.Path);
                await using var file = File.OpenRead(path);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) != entry.Sha256)
                    throw new InvalidDataException("Extracted Mac file checksum changed: " + entry.Path);
            }
            if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                && (File.GetUnixFileMode(path) & (UnixFileMode)0x1ff) != (entry.Mode & (UnixFileMode)0x1ff))
                throw new InvalidDataException("Extracted Mac permissions changed: " + entry.Path);
        }
        var pending = new Stack<string>(); pending.Push(payload);
        while (pending.TryPop(out string? directory))
            foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(payload, path);
                var attributes = File.GetAttributes(path);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                bool link = (attributes & FileAttributes.ReparsePoint) != 0;
                if ((!expected.ContainsKey(relative) && (!isDirectory || !directories.Contains(relative)))
                    || (expected.TryGetValue(relative, out var entry) && entry.AppleDouble))
                    throw new InvalidDataException("Unexpected extracted Mac payload entry: " + relative);
                if (isDirectory && !link) pending.Push(path);
            }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        {
            if (count > 64 * 1024 - text.Length) throw new InvalidDataException("Mac staging diagnostic exceeds its limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static void RemoveOwnedTree(string root)
    {
        if (!System.IO.Directory.Exists(root)) return;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(directory, File.GetUnixFileMode(directory)
                | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (string path in System.IO.Directory.EnumerateDirectories(directory))
                if (new DirectoryInfo(path).LinkTarget is null) pending.Push(path);
        }
        System.IO.Directory.Delete(root, recursive: true);
    }
}
