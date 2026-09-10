using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsUpdateStagerTests
{
    public TestContext TestContext { get; set; } = null!;
    private const string Commit = "1111111111111111111111111111111111111111";
    private static readonly string[] Binaries = ["kicad.exe", "kicad-cli.exe", "kicad-mcp.exe", "coreclr.dll", "nng.dll"];

    [TestMethod]
    public async Task ExtractsOnlyACompleteVerifiedPlanIntoANewDirectory()
    {
        string scratch = Directory.CreateTempSubdirectory("kwextract-").FullName;
        try
        {
            using var archive = Zip("fixture bytes"u8.ToArray(), "normal");
            string destination = Path.Combine(scratch, "payload");
            var plan = await WindowsUpdateStager.ExtractAsync(archive, destination, default);
            Assert.AreEqual(6, plan.Entries.Count);
            foreach (var file in plan.Entries)
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(destination, file.Path));
                Assert.AreEqual(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Assert.AreEqual(file.Bytes, bytes.LongLength);
            }
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => WindowsUpdateStager.ExtractAsync(archive, destination, default));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "bin/kicad.exe")));
        }
        finally { Directory.Delete(scratch, true); }
    }

    [TestMethod]
    public async Task InvalidArchivesAndCancelledCallsDoNotCreateOutputOrChangeSiblings()
    {
        string scratch = Directory.CreateTempSubdirectory("kwextract-").FullName;
        try
        {
            string sentinel = Path.Combine(scratch, "user-work"); await File.WriteAllTextAsync(sentinel, "unchanged");
            using var archive = Zip("fixture"u8.ToArray(), "normal", escaping: true);
            string output = Path.Combine(scratch, "rejected");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsUpdateStager.ExtractAsync(archive, output, default));
            Assert.IsFalse(Directory.Exists(output));
            using var valid = Zip("fixture"u8.ToArray(), "normal");
            await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsUpdateStager.ExtractAsync(valid, output, new CancellationToken(true)));
            Assert.IsFalse(Directory.Exists(output));
            Assert.AreEqual("unchanged", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(scratch, true); }
    }

    [TestMethod]
    public async Task NonWindowsCannotClaimNativeRuntimeStaging()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("This guard is for other platforms."); return; }
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => WindowsUpdateStager.StageAsync(null!, null!, "/tmp"));
    }

    [TestMethod]
    [DataRow("processArchitecture", "Arm64")]
    [DataRow("framework", ".NET 9.0")]
    [DataRow("status", "loading")]
    [DataRow("nngVersion", "")]
    public void RejectsUnavailableOrMismatchedRuntimeResults(string field, string value)
    {
        var result = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["status"] = "runtime_available", ["processArchitecture"] = "X64",
            ["framework"] = ".NET 10.0", ["nngVersion"] = "fixture", ["nativeEditorContacted"] = false, ["crossPlatformReady"] = false
        };
        WindowsUpdateStager.RequireRuntime(JsonSerializer.Serialize(result));
        result[field] = value;
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsUpdateStager.RequireRuntime(JsonSerializer.Serialize(result)));
    }

    [TestMethod]
    public async Task NativeFixtureExercisesStagingFailureCancellationAndRecovery()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows executable and loader."); return; }
        string scratch = Directory.CreateTempSubdirectory("kwstage-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "windows-update-staging")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            byte[] executable = await Compile(scratch, evidence, deadline.Token);
            string sentinel = Path.Combine(scratch, "existing-user-work"); await File.WriteAllTextAsync(sentinel, "keep");
            var (manifest, download) = await Package("normal", Commit);
            var staged = await WindowsUpdateStager.StageAsync(manifest, download, scratch, deadline.Token);
            Assert.AreEqual("win-x64", staged.Platform); Assert.AreEqual(manifest.PayloadSha256, staged.ManifestSha256);
            File.Copy(Path.Combine(Path.GetDirectoryName(staged.Directory)!, "staging.json"), Path.Combine(evidence, "staging.json"));
            using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "staging.json"))))
            {
                Assert.IsFalse(receipt.RootElement.GetProperty("installationReady").GetBoolean());
                Assert.IsFalse(receipt.RootElement.GetProperty("authenticodeVerified").GetBoolean());
                Assert.IsTrue(receipt.RootElement.GetProperty("nativeRuntimeVerified").GetBoolean());
            }
            foreach (string mode in new[] { "fail", "excess-output", "wrong-commit", "wrong-hash" })
            {
                var bad = await Package(mode == "wrong-commit" ? "normal" : mode, mode == "wrong-commit" ? new string('2', 40) : Commit);
                if (mode == "wrong-hash") await File.AppendAllTextAsync(bad.Download.Path, "changed bytes");
                await Assert.ThrowsExactlyAsync<IOException>(() => WindowsUpdateStager.StageAsync(bad.Manifest, bad.Download, scratch, deadline.Token));
                Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
                Assert.IsTrue(File.Exists(Path.Combine(staged.Directory, "bin/kicad.exe")));
            }
            var waiting = await Package("wait", Commit);
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsUpdateStager.StageAsync(waiting.Manifest, waiting.Download, scratch, cancellation.Token));
            Assert.AreEqual(1, Directory.GetDirectories(scratch, "windows-stage-*").Length, "Failed partial candidates must be removed, not the prior valid stage.");
            var recovered = await WindowsUpdateStager.StageAsync(manifest, download, scratch, deadline.Token);
            Assert.AreNotEqual(staged.Directory, recovered.Directory); Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
            await File.WriteAllTextAsync(Path.Combine(evidence, "fixture-result.json"),
                "{\"schemaVersion\":1,\"status\":\"passed\",\"syntheticExecutableFixture\":true,\"realKiCadRuntimeVerified\":false,\"installationReady\":false}");

            async Task<(VerifiedUpdateManifest Manifest, DownloadedUpdate Download)> Package(string mode, string declaredCommit)
            {
                using var zip = Zip(executable, mode); byte[] bytes = zip.ToArray();
                var artifact = new UpdateArtifact("win-x64", "zip", "fixture.zip", bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "synthetic-staging-fixture", declaredCommit, [artifact]);
                var verified = UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, key), key.ExportSubjectPublicKeyInfo(), "preview");
                string path = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".zip");
                await File.WriteAllBytesAsync(path, bytes, deadline.Token);
                return (verified, new(path, artifact, verified.PayloadSha256));
            }
        }
        finally
        {
            foreach (string directory in Directory.GetDirectories(scratch, "windows-update-diagnostics-*"))
            {
                string target = Directory.CreateDirectory(Path.Combine(evidence, Path.GetFileName(directory))).FullName;
                foreach (string file in Directory.GetFiles(directory)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            foreach (string file in Directory.GetFiles(evidence, "*", SearchOption.AllDirectories)) TestContext.AddResultFile(file);
            Directory.Delete(scratch, true);
        }
    }

    private static MemoryStream Zip(byte[] executable, string mode, bool escaping = false)
    {
        var input = new MemoryStream();
        using (var zip = new ZipArchive(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in Binaries)
            { using var file = zip.CreateEntry("bin/" + name).Open(); file.Write(executable); }
            using (var policy = new StreamWriter(zip.CreateEntry("bin/fixture-policy.txt").Open()))
            { policy.WriteLine(mode); policy.WriteLine(Commit); }
            if (escaping) { using var file = zip.CreateEntry("../escape").Open(); file.WriteByte(1); }
        }
        input.Position = 0; return input;
    }

    private static async Task<byte[]> Compile(string scratch, string evidence, CancellationToken token)
    {
        DirectoryInfo? source = new(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "automation/tests/fixtures/windows-staging-probe.cpp"))) source = source.Parent;
        Assert.IsNotNull(source);
        string executable = Path.Combine(scratch, "probe.exe");
        var start = new ProcessStartInfo("cl.exe") { WorkingDirectory = scratch, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/nologo", "/EHsc", "/std:c++17", "/MT",
            Path.Combine(source.FullName, "automation/tests/fixtures/windows-staging-probe.cpp"), "/Fe:" + executable }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        finally
        {
            if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode);
        return await File.ReadAllBytesAsync(executable, token);
    }
}
