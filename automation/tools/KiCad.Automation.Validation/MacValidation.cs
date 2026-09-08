using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KiCad.Automation.Validation;

public sealed record MacRequest(string Repository, string Commit, string Builder, string Toolchain,
    string Output, string NativeTests, string Architecture);

public static class MacValidation
{
    public static async Task<ValidationResult> RunAsync(MacRequest request, CancellationToken cancellationToken)
    {
        // SA-06: no remote worker, clean-slate bootstrap or modification of an
        // existing worktree. Only a dedicated new output tree is created.
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The mac command must execute on the Mac. Linux cannot produce native-Mac execution evidence.");
        Evidence.RequireCommit(request.Commit);
        MacArchitecture.RequireExecutionTarget(request.Architecture, RuntimeInformation.ProcessArchitecture);
        string repository = Path.GetFullPath(request.Repository);
        string builder = Path.GetFullPath(request.Builder);
        string toolchain = Path.GetFullPath(request.Toolchain);
        string output = Path.GetFullPath(request.Output);
        RequireInstallOutputPath(output);
        if (!Directory.Exists(repository) || !Directory.Exists(builder) || !File.Exists(toolchain))
            throw new ArgumentException("Provide existing repository, KiCad Mac Builder checkout and its configured CMake toolchain.");
        if (Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("The output directory must not exist. Existing worktrees and caches are never cleaned or overwritten.");
        if (string.IsNullOrWhiteSpace(request.NativeTests)) throw new ArgumentException("A nonempty CTest selection is required.");
        Directory.CreateDirectory(output);
        string evidence = Directory.CreateDirectory(Path.Combine(output, "evidence")).FullName;
        string source = Path.Combine(output, "source");
        string build = Path.Combine(output, "build");
        string toolchainHash = Evidence.Hash(toolchain);
        string wrapper = Path.Combine(evidence, "validation-toolchain.cmake");
        // The official generated toolchain sets its own install prefix. Override
        // it after inclusion so validation cannot install into the builder tree.
        await File.WriteAllTextAsync(wrapper,
            $"include({CmakeLiteral(toolchain)})\nset(CMAKE_INSTALL_PREFIX {CmakeLiteral(Path.Combine(output, "install"))} CACHE PATH \"\" FORCE)\nset(CMAKE_INSTALL_PREFIX {CmakeLiteral(Path.Combine(output, "install"))})\n"
            + $"set(CMAKE_OSX_ARCHITECTURES {CmakeLiteral(MacArchitecture.CmakeName(request.Architecture))} CACHE STRING \"\" FORCE)\n"
            + $"set(CMAKE_OSX_ARCHITECTURES {CmakeLiteral(MacArchitecture.CmakeName(request.Architecture))})\n");
        File.Copy(toolchain, Path.Combine(evidence, "builder-toolchain.cmake"));
        string? verified = null, builderCommit = null, builderStatus = null, failure = null;
        string status = "failed";
        const string dotnetSelection = "TestCategory!=NativeSession";
        var steps = new List<ValidationStep>();
        var binaries = new List<NativeBinaryEvidence>();
        var environment = new Dictionary<string, string>
        {
            ["KICAD_AUTOMATION_EVIDENCE_DIRECTORY"] = evidence,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1",
            ["KICAD_CONFIG_HOME"] = Path.Combine(output, "test-config"),
            ["KICAD_CACHE_HOME"] = Path.Combine(output, "test-cache")
        };
        try
        {
            builderCommit = (await Run("builder-commit", "git", ["-C", builder, "rev-parse", "HEAD"], true)).Trim();
            Evidence.RequireCommit(builderCommit);
            builderStatus = await Run("builder-status", "git", ["-C", builder, "status", "--porcelain"], true);
            await Run("fetch", "git", ["-C", repository, "fetch", "origin", request.Commit]);
            verified = (await Run("verify-source", "git", ["-C", repository, "rev-parse", "--verify", request.Commit + "^{commit}"], true)).Trim();
            if (verified != request.Commit) throw new InvalidDataException("Fetched source does not match the requested commit.");
            await Run("worktree", "git", ["-C", repository, "worktree", "add", "--detach", source, verified]);
            await Run("platform", "sw_vers", []);
            await Run("compiler", "xcrun", ["clang", "--version"]);
            await Run("cmake-version", "cmake", ["--version"]);
            // Source-page tests execute these native utilities. Capture their
            // actual installed versions; never bootstrap or install them here.
            await Run("pdf-text-version", "pdftotext", ["-v"]);
            await Run("pdf-render-version", "pdftoppm", ["-v"]);
            string automation = Path.Combine(source, "automation");
            await Run("dotnet-version", "dotnet", ["--info"], workingDirectory: automation);
            await Run("configure", "cmake", ["-S", source, "-B", build, "-G", "Ninja",
                "-DCMAKE_TOOLCHAIN_FILE=" + wrapper, "-DCMAKE_BUILD_TYPE=Debug", "-DKICAD_BUILD_QA_TESTS=ON",
                "-DCMAKE_INSTALL_PREFIX=" + Path.Combine(output, "install"),
                "-DCMAKE_OSX_ARCHITECTURES=" + MacArchitecture.CmakeName(request.Architecture)]);
            File.Copy(Path.Combine(build, "CMakeCache.txt"), Path.Combine(evidence, "CMakeCache.txt"));
            await Run("native-build", "cmake", ["--build", build]);
            // Use the pinned project's own bundle/dependency installation rules.
            // The wrapper above confines their absolute destinations to this run.
            await Run("native-install", "cmake", ["--install", build]);
            string nativeBin = Path.Combine(output, "install", "KiCad.app", "Contents", "MacOS");
            foreach (string name in new[] { "kicad", "kicad-cli" })
            {
                string executable = Path.Combine(nativeBin, name);
                string slices = (await Run(name + "-architecture", "xcrun",
                    ["lipo", "-archs", executable], capture: true)).Trim();
                MacArchitecture.RequireBinaryTarget(request.Architecture, slices);
                binaries.Add(new(name, slices, Evidence.Hash(executable)));
            }
            string nativeCommit = (await Run("installed-native-commit", "arch",
                ["-" + MacArchitecture.CmakeName(request.Architecture), Path.Combine(nativeBin, "kicad-cli"),
                 "version", "--format", "commit"], capture: true)).Trim();
            if (nativeCommit != request.Commit)
                throw new InvalidDataException("Installed native KiCad does not report the requested commit.");
            await Run("native-tests", "ctest", ["--test-dir", build, "--no-tests=error", "--output-on-failure",
                "-R", request.NativeTests, "--output-junit", Path.Combine(evidence, "native-tests.xml")]);
            string solution = Path.Combine(source, "automation", "KiCad.Automation.slnx");
            await Run("restore", "dotnet", ["restore", solution, "--locked-mode"], workingDirectory: automation);
            await Run("dotnet-tests", "dotnet", ["test", solution, "--no-restore", "--configuration", "Debug",
                "--filter", dotnetSelection, "--logger", "trx", "--results-directory", Path.Combine(evidence, "dotnet-tests")], workingDirectory: automation);
            await Run("source-unchanged", "git", ["-C", source, "diff", "--exit-code", "HEAD"]);
            foreach (var binary in binaries)
                if (Evidence.Hash(Path.Combine(nativeBin, binary.Name)) != binary.Sha256)
                    throw new InvalidDataException("A native executable changed during validation.");
            if (toolchainHash != Evidence.Hash(toolchain)) throw new InvalidDataException("The toolchain changed during validation.");
            if ((await Run("builder-unchanged", "git", ["-C", builder, "rev-parse", "HEAD"], true)).Trim() != builderCommit)
                throw new InvalidDataException("The builder commit changed during validation.");
            status = "checks_passed";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            status = error is OperationCanceledException ? "cancelled" : "failed";
            failure = error.Message;
        }
        // Cancellation/failure still produces a receipt and preserves all logs.
        EvidenceFile[] files = Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new EvidenceFile(Path.GetRelativePath(evidence, path).Replace('\\', '/'),
                new FileInfo(path).Length, Evidence.Hash(path))).ToArray();
        var receipt = new ValidationReceipt(2, "macos", RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSArchitecture.ToString(), request.Commit, verified, builderCommit, builderStatus,
            toolchainHash, request.NativeTests, dotnetSelection, status, failure, steps, files,
            request.Architecture, binaries);
        return await Evidence.SealAsync(output, evidence, receipt);

        async Task<string> Run(string name, string executable, string[] arguments, bool capture = false, string? workingDirectory = null)
        {
            string prefix = $"{steps.Count:D2}-{name}";
            string stdout = Path.Combine(evidence, prefix + ".stdout.log");
            string stderr = Path.Combine(evidence, prefix + ".stderr.log");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory ?? repository, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            DateTimeOffset began = DateTimeOffset.UtcNow;
            using Process process = Process.Start(start) ?? throw new IOException($"Could not start {name}.");
            await using var standardOut = File.Create(stdout);
            await using var standardError = File.Create(stderr);
            Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(standardOut);
            Task copyError = process.StandardError.BaseStream.CopyToAsync(standardError);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                // These are only this command's build/test subprocesses.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            finally
            {
                await Task.WhenAll(copyOut, copyError);
                await standardOut.FlushAsync();
                await standardError.FlushAsync();
                steps.Add(new(name, process.ExitCode, began, DateTimeOffset.UtcNow,
                    Path.GetFileName(stdout), Path.GetFileName(stderr)));
            }
            if (process.ExitCode != 0) throw new IOException($"{name} exited with code {process.ExitCode}; see {prefix} logs.");
            if (!capture) return "";
            await standardOut.DisposeAsync();
            await standardError.DisposeAsync();
            if (new FileInfo(stdout).Length > 65536) throw new InvalidDataException($"Unexpectedly large {name} metadata.");
            return await File.ReadAllTextAsync(stdout, cancellationToken);
        }
    }

    public static string CmakeLiteral(string value)
    {
        string equals = "=";
        while (value.Contains("]" + equals + "]", StringComparison.Ordinal)) equals += "=";
        return "[" + equals + "[" + value + "]" + equals + "]";
    }

    public static void RequireInstallOutputPath(string output)
    {
        // The pinned native install(CODE) strings interpolate this prefix into
        // unquoted CMake operations, including REMOVE_RECURSE. Do not let a
        // literal destination become several arguments or generated CMake code.
        if (!Path.IsPathFullyQualified(output)
            || output.Any(c => char.IsWhiteSpace(c) || ";\"'\\$()[]#{}".Contains(c)))
            throw new ArgumentException("The pinned Mac installer requires a dedicated absolute output path without spaces or CMake metacharacters.");
    }
}
