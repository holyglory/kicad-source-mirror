using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KiCad.Automation.Validation;

public sealed record LinuxStageRequest(string NativeBuild, string ManagedPublish,
    string NngLibrary, string OutputRoot);
public sealed record LinuxStageResult(int SchemaVersion, string Status, string Directory,
    string? Failure, IReadOnlyList<EvidenceFile> Files, bool QualifyingDelivery = false);

/// <summary>Creates an isolated install tree, never installs into the host prefix.
/// This is private package preparation, not publication or update qualification.</summary>
public static class LinuxStaging
{
    public static readonly string[] RequiredExecutables =
        ["kicad", "kicad-cli", "eeschema", "pcbnew", "gerbview", "pl_editor",
         "pcb_calculator", "bitmap2component"];
    public static readonly string[] RequiredInterfaces =
        ["eeschema", "pcbnew", "gerbview", "pl_editor", "pcb_calculator", "cvpcb"];

    public static void ValidateInputs(LinuxStageRequest request)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Linux x64 staging must run on Linux x64.");
        foreach (string path in new[] { request.NativeBuild, request.ManagedPublish,
                     request.NngLibrary, request.OutputRoot })
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("Staging paths must be absolute.");
        if (!File.Exists(Path.Combine(request.NativeBuild, "cmake_install.cmake")))
            throw new ArgumentException("The native build must contain its generated install rules.");
        foreach (string name in new[] { "kicad-mcp", "kicad-mcp.dll", "libcoreclr.so", "libhostpolicy.so" })
            if (!File.Exists(Path.Combine(request.ManagedPublish, name)))
                throw new ArgumentException($"Self-contained MCP publish output is missing {name}.");
        if (!File.Exists(request.NngLibrary)) throw new ArgumentException("Provide the NNG shared library used by the MCP runtime.");
        if (!Directory.Exists(request.OutputRoot))
            throw new ArgumentException("Provide an existing dedicated package staging directory.");
        if (Path.GetPathRoot(request.OutputRoot) == Path.TrimEndingDirectorySeparator(request.OutputRoot))
            throw new ArgumentException("A filesystem root is not a package staging directory.");
    }

    public static async Task<LinuxStageResult> RunAsync(LinuxStageRequest request, CancellationToken cancellationToken)
    {
        ValidateInputs(request);
        cancellationToken.ThrowIfCancellationRequested();
        string output = Directory.CreateDirectory(Path.Combine(request.OutputRoot,
            "linux-x64-" + Guid.NewGuid().ToString("N"))).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(output, "root")).FullName;
        string prefix = Path.Combine(destination, "usr", "local");
        string? failure = null;
        string status = "failed";
        EvidenceFile[] files = [];
        try
        {
            var start = new ProcessStartInfo("cmake")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "--install", request.NativeBuild, "--prefix", "/usr/local", "--strip" })
                start.ArgumentList.Add(arg);
            // Existing KiCad rules include absolute data paths. DESTDIR contains
            // those too; --prefix by itself would write into the host /usr/local.
            start.Environment["DESTDIR"] = destination;
            using (Process process = Process.Start(start) ?? throw new IOException("Could not start native staging."))
            {
                await using var stdout = File.Create(Path.Combine(output, "install.stdout.log"));
                await using var stderr = File.Create(Path.Combine(output, "install.stderr.log"));
                Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                Task copyError = process.StandardError.BaseStream.CopyToAsync(stderr);
                try { await process.WaitForExitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw;
                }
                finally { await Task.WhenAll(copyOut, copyError); }
                if (process.ExitCode != 0) throw new IOException($"Native install exited {process.ExitCode}; inspect retained install logs.");
            }

            string managed = Directory.CreateDirectory(Path.Combine(prefix, "lib", "kicad-automation")).FullName;
            CopyTree(request.ManagedPublish, managed, cancellationToken);
            File.Copy(request.NngLibrary, Path.Combine(managed, "libnng.so"), overwrite: false);
            VerifyTree(prefix);
            files = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new EvidenceFile(Path.GetRelativePath(destination, path).Replace('\\', '/'),
                        new FileInfo(path).Length, Evidence.Hash(path));
                }).ToArray();
            status = "staged";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            status = error is OperationCanceledException ? "cancelled" : "failed";
            failure = error.Message;
        }
        var result = new LinuxStageResult(1, status, output, failure, files);
        await File.WriteAllTextAsync(Path.Combine(output, "staging.json"), JsonSerializer.Serialize(result, Evidence.JsonOptions));
        // The per-candidate receipt above is permanent. This generated pointer
        // lets a dependent governed check inspect precisely the newly staged tree.
        string pointer = Path.Combine(request.OutputRoot, ".current-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(pointer, JsonSerializer.Serialize(result, Evidence.JsonOptions));
        File.Move(pointer, Path.Combine(request.OutputRoot, "current-staging.json"), overwrite: true);
        return result;
    }

    public static void VerifyTree(string prefix)
    {
        foreach (string name in RequiredExecutables)
            Require(Path.Combine(prefix, "bin", name));
        foreach (string name in RequiredInterfaces)
            Require(Path.Combine(prefix, "bin", "_" + name + ".kiface"));
        Require(Path.Combine(prefix, "lib", "libkicommon.so"));
        Require(Path.Combine(prefix, "lib", "libkiapi.so"));
        foreach (string name in new[] { "kicad-mcp", "kicad-mcp.dll", "libcoreclr.so", "libhostpolicy.so", "libnng.so" })
            Require(Path.Combine(prefix, "lib", "kicad-automation", name));
        if (!Directory.Exists(Path.Combine(prefix, "share", "kicad")))
            throw new InvalidDataException("Native KiCad application data is missing from the install tree.");

        static void Require(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException($"Required package file is missing or empty: {path}");
        }
    }

    private static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }
}
