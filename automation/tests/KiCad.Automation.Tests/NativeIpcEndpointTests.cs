using System.Diagnostics;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeIpcEndpointTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeAndManagedNamesAgreeAndWindowsPipeReallyExchangesBytes()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "include/api/api_socket_url.h")))
            repository = repository.Parent;
        Assert.IsNotNull(repository);
        string scratch = Directory.CreateTempSubdirectory("kipc-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE") ?? TestContext.TestResultsDirectory!,
            "native-ipc-endpoint")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            string source = Path.Combine(repository.FullName, "automation/tests/fixtures/native-ipc-endpoint.cpp");
            string executable = Path.Combine(scratch, OperatingSystem.IsWindows() ? "endpoint.exe" : "endpoint");
            string include = Path.Combine(repository.FullName, "include");
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/EHsc", "/std:c++17", "/I" + include, source, "/Fe:" + executable]
                : ["-std=c++17", "-I", include, source, "-o", executable];
            Assert.AreEqual(0, await Run("compile", OperatingSystem.IsWindows() ? "cl.exe" : "c++", arguments));
            string path = Path.Combine(scratch, "literal % space", Guid.NewGuid().ToString("D") + ".sock");
            Assert.AreEqual(0, await Run("native", executable, [path, NativeIpcEndpoint.FromSocketPath(path)]));
            string result = await File.ReadAllTextAsync(Path.Combine(evidence, "native.stdout.log"));
            StringAssert.Contains(result, OperatingSystem.IsWindows()
                ? "native-pipe-exchange-and-wrong-target-rejection" : "native-posix-endpoint-unchanged");
        }
        finally { Directory.Delete(scratch, true); }

        async Task<int> Run(string name, string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = scratch, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start " + name);
            await using var stdout = File.Create(Path.Combine(evidence, name + ".stdout.log"));
            await using var stderr = File.Create(Path.Combine(evidence, name + ".stderr.log"));
            Task output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task error = process.StandardError.BaseStream.CopyToAsync(stderr);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
            }
            TestContext.AddResultFile(Path.Combine(evidence, name + ".stdout.log"));
            TestContext.AddResultFile(Path.Combine(evidence, name + ".stderr.log"));
            return process.ExitCode;
        }
    }

    [TestMethod]
    public void InstanceEndpointsAreAbsoluteDistinctAndDoNotDependOnWorkingDirectory()
    {
        string id = Guid.NewGuid().ToString("D");
        string runtime = NativeIpcEndpoint.RuntimeDirectory(id);
        Assert.IsTrue(Path.IsPathFullyQualified(runtime));
        Assert.AreEqual(id, Path.GetFileName(runtime));
        Assert.AreNotEqual(runtime, NativeIpcEndpoint.RuntimeDirectory(Guid.NewGuid().ToString("D")));
        string socket = Path.Combine(runtime, "api.sock");
        string endpoint = NativeIpcEndpoint.FromSocketPath(socket);
        NngTransport.ValidateEndpoint(endpoint);
        Assert.AreEqual("ipc://" + socket, endpoint);
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual("/tmp/kicad-automation/" + id + "/api.sock", socket);
    }

    [TestMethod]
    public void LiteralNamesAreNotUriEscaped()
    {
        string socket = Path.Combine(Path.GetTempPath(), "kicad % fixture", "api.sock");
        StringAssert.Contains(NativeIpcEndpoint.FromSocketPath(socket), "kicad % fixture");
    }

    [TestMethod]
    [DataRow("ipc://C:/Users/runner/AppData/Local/Temp/kicad/api.sock")]
    [DataRow("ipc://C:\\Users\\runner\\AppData\\Local\\Temp\\kicad\\api.sock")]
    public void WindowsDriveEndpointsAreAcceptedOnlyOnWindows(string endpoint)
    {
        if (OperatingSystem.IsWindows()) NngTransport.ValidateEndpoint(endpoint);
        else Assert.ThrowsExactly<ArgumentException>(() => NngTransport.ValidateEndpoint(endpoint));
    }

    [TestMethod]
    [DataRow("tcp://localhost:9999")]
    [DataRow("ipc://relative")]
    [DataRow("ipc://C:relative")]
    [DataRow("ipc://C:")]
    [DataRow("ipc:///")]
    [DataRow("ipc:///tmp/a\0b")]
    public void InvalidOrNonlocalAddressesAreRejected(string endpoint) =>
        Assert.ThrowsExactly<ArgumentException>(() => NngTransport.ValidateEndpoint(endpoint));

    [TestMethod]
    public void RelativeSocketPathsAndInvalidInstanceIdsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => NativeIpcEndpoint.FromSocketPath("relative/api.sock"));
        Assert.ThrowsExactly<ArgumentException>(() => NativeIpcEndpoint.RuntimeDirectory("../other"));
    }
}
