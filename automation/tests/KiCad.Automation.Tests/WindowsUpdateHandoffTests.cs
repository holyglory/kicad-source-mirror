using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsUpdateHandoffTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task NonWindowsCannotPerformAWindowsHandoff()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Other-platform guard."); return; }
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => WindowsUpdateHandoff.ExecuteAsync(null!, _ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task NativePreflightAndCancellationDoNotCloseOrReplaceTheOriginalProcess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows process and file semantics."); return; }
        string root = Directory.CreateTempSubdirectory("kwhandoff-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-handoff-preflight")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Process? process = null;
        try
        {
            byte[] binary = await WindowsUpdateStagerTests.Compile(root, evidence, deadline.Token);
            using var zip = WindowsUpdateStagerTests.Zip(binary, "normal"); byte[] bytes = zip.ToArray();
            string archive = Path.Combine(root, "fixture.zip"); await File.WriteAllBytesAsync(archive, bytes, deadline.Token);
            var artifact = new UpdateArtifact("win-x64", "zip", "fixture.zip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "handoff-fixture-one", new string('1', 40), [artifact]);
            string installation = Path.Combine(root, "installed");
            var original = await WindowsVerifiedVersions.CreateAsync(installation, archive, UpdateManifestCodec.Sign(release, publisher),
                publisher.ExportSubjectPublicKeyInfo(), new Uri("https://fixture.invalid/"), "preview", deadline.Token);
            var candidate = await WindowsVerifiedVersions.RegisterAsync(installation, original.SelectionId, archive,
                UpdateManifestCodec.Sign(release with { Sequence = 2, Version = "handoff-fixture-two" }, publisher), deadline.Token);
            var start = new ProcessStartInfo(original.Version.NativeExecutable) { UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--stay"); process = Process.Start(start)!;
            Assert.AreEqual("ready", await process.StandardOutput.ReadLineAsync(deadline.Token));
            var identity = WindowsProcessIdentity.Read(process.Id);
            // Windows NNG uses this as a local named-pipe name; no file is written at the drive root.
            string socket = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "kwh-" + Guid.NewGuid().ToString("N")[..16] + ".sock");
            var request = new WindowsUpdateHandoffRequest(installation, original.SelectionId, candidate.Version.ManifestSha256,
                Guid.NewGuid(), identity, "", Guid.NewGuid(), socket, false);
            var wrong = request with { OldProcess = identity with { CreationFileTime = "1" } };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsUpdateHandoff.ExecuteAsync(wrong, _ => Task.CompletedTask, deadline.Token));
            Assert.IsFalse(Directory.Exists(Path.Combine(installation, "handovers", wrong.OperationId.ToString("D"))));
            using var cancelled = new CancellationTokenSource();
            bool acknowledged = false;
            var result = await WindowsUpdateHandoff.ExecuteAsync(request, state =>
            {
                Assert.AreEqual("waiting_for_exit", state.Status); acknowledged = true;
                Assert.IsTrue(File.Exists(Path.Combine(state.JournalDirectory, "intent.json")));
                cancelled.Cancel(); return Task.CompletedTask;
            }, cancelled.Token);
            Assert.IsTrue(acknowledged); Assert.AreEqual("cancelled_before_activation", result.Status);
            Assert.IsFalse(process.HasExited); Assert.AreEqual(identity, WindowsProcessIdentity.Read(process.Id));
            Assert.AreEqual(original, await WindowsVerifiedVersions.InspectCurrentAsync(installation, deadline.Token));
            var repeated = await WindowsUpdateHandoff.ExecuteAsync(request, _ => throw new AssertFailedException("Existing handoffs cannot acknowledge a new close."), deadline.Token);
            Assert.AreEqual("reconciliation_required", repeated.Status);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WindowsUpdateHandoff.ExecuteAsync(request with { SocketPath = socket + "x" },
                _ => Task.CompletedTask, deadline.Token));
            var commandRequest = request with { OperationId = Guid.NewGuid() };
            string commandPath = Path.Combine(root, "restart.json");
            await File.WriteAllTextAsync(commandPath, JsonSerializer.Serialize(new WindowsRestartConfiguration(3, commandRequest),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            using var commandCancel = new CancellationTokenSource();
            using var writer = new AcknowledgmentWriter(commandCancel);
            Assert.AreEqual(2, await WindowsRestartCommand.RunAsync(["--restart-update", "--configuration", commandPath], writer, commandCancel.Token));
            Assert.AreEqual(1, writer.Lines, "After acknowledging close, completion must not depend on the old window's output pipe.");
            Assert.IsFalse(process.HasExited);
            Assert.AreEqual(original, await WindowsVerifiedVersions.InspectCurrentAsync(installation, deadline.Token));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = "passed", syntheticNativePayload = true, exactProcessBound = true,
                intentPersistedBeforeCloseAcknowledgment = true, cancelledOriginalPreserved = true,
                duplicateHandoffNotReexecuted = true, nativeEditorRestarted = false, fullRestartRecoveryVerified = false
            }), deadline.Token);
            TestContext.AddResultFile(Path.Combine(evidence, "result.json"));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.StandardInput.Close(); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
        }
    }

    private sealed class AcknowledgmentWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public int Lines { get; private set; }
        public override Task WriteLineAsync(string? value)
        {
            Lines++;
            if (Lines != 1) throw new IOException("The acknowledged window's pipe is closed.");
            using var result = JsonDocument.Parse(value!);
            Assert.AreEqual("waiting_for_exit", result.RootElement.GetProperty("state").GetProperty("status").GetString());
            cancellation.Cancel(); return base.WriteLineAsync(value);
        }
    }
}
