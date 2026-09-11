using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacProcessIdentityTests
{
    [TestMethod]
    public void ExactKernelTimeFieldsRejectWrongProcessZombieAndTruncation()
    {
        byte[] bsd = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(bsd.AsSpan(12), 123);
        BinaryPrimitives.WriteUInt64LittleEndian(bsd.AsSpan(120), 1700000000);
        BinaryPrimitives.WriteUInt64LittleEndian(bsd.AsSpan(128), 345678);
        Guid boot = Guid.NewGuid();
        string path = Path.GetFullPath("fixture-native");
        var identity = MacProcessIdentity.Decode(bsd, 123, boot, path);
        Assert.AreEqual(1700000000UL, identity.StartSeconds);
        Assert.AreEqual(345678UL, identity.StartMicroseconds);
        Assert.ThrowsExactly<InvalidDataException>(() => MacProcessIdentity.Decode(bsd, 124, boot, path));
        Assert.ThrowsExactly<InvalidDataException>(() => MacProcessIdentity.Decode(bsd[..120], 123, boot, path));
        BinaryPrimitives.WriteUInt32LittleEndian(bsd.AsSpan(4), 5);
        Assert.ThrowsExactly<InvalidDataException>(() => MacProcessIdentity.Decode(bsd, 123, boot, path));
        BinaryPrimitives.WriteUInt32LittleEndian(bsd.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bsd.AsSpan(128), 1000000);
        Assert.ThrowsExactly<InvalidDataException>(() => MacProcessIdentity.Decode(bsd, 123, boot, path));
        if (!OperatingSystem.IsMacOS()) Assert.ThrowsExactly<PlatformNotSupportedException>(() => MacProcessIdentity.Read(Environment.ProcessId));
    }

    [TestMethod]
    public async Task NativeMacHeadersAndManagedBindingIdentifyTheSameProcess()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Inconclusive("Real Mac process identity requires macOS."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-process-id-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            string probe = await CompileProbe(root, deadline.Token);
            string output = await Run(probe, ["identity", Environment.ProcessId.ToString()], deadline.Token);
            using var json = JsonDocument.Parse(output);
            Assert.AreEqual(136, json.RootElement.GetProperty("structureSize").GetInt32());
            Assert.AreEqual(120, json.RootElement.GetProperty("secondsOffset").GetInt32());
            Assert.AreEqual(128, json.RootElement.GetProperty("microsecondsOffset").GetInt32());
            var native = JsonSerializer.Deserialize<MacProcessIdentity>(output, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.AreEqual(native, MacProcessIdentity.Read(Environment.ProcessId));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MacProcessIdentity.Read(0));
            using var child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
            await child.WaitForExitAsync(deadline.Token);
            Assert.ThrowsExactly<IOException>(() => MacProcessIdentity.Read(child.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static async Task<string> CompileProbe(string directory, CancellationToken token)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "automation/tests/fixtures/mac-process-probe.mm"))) root = root.Parent;
        Assert.IsNotNull(root);
        string executable = Path.Combine(directory, "mac-process-probe");
        _ = await Run("/usr/bin/xcrun", ["clang++", "-std=c++17", "-framework", "AppKit",
            Path.Combine(root.FullName, "automation/tests/fixtures/mac-process-probe.mm"), "-o", executable], token);
        return executable;
    }

    internal static async Task<string> Run(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token), stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            string error = await stderr;
            string output = await stdout;
            Assert.AreEqual(0, process.ExitCode, error + "\n" + (output.Length <= 4096 ? output : output[^4096..]));
            return output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}
