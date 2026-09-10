using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

/// <summary>Bounded, query-only external COM observation. Never closes or
/// signals the editor; cancellation owns only the short-lived observer.</summary>
internal sealed class WindowsUiObserver(string executable, string scratch)
{
    public static async Task<WindowsUiObserver> CreateAsync(string scratch, string evidence, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows UI observation requires Windows.");
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "automation/tests/fixtures/windows-ui-observer.cpp"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        string executable = Path.Combine(scratch, "ui-observer.exe");
        var result = await WindowsLauncherTests.Invoke("cl.exe", ["/nologo", "/EHsc", "/std:c++20", "/MT", "/utf-8", "/DUNICODE", "/D_UNICODE",
            "/I" + Path.Combine(repository.FullName, "thirdparty/nlohmann_json"),
            Path.Combine(repository.FullName, "automation/tests/fixtures/windows-ui-observer.cpp"), "/Fe:" + executable,
            "user32.lib", "oleacc.lib", "ole32.lib", "oleaut32.lib", "uuid.lib"], scratch, token, input: null);
        await File.WriteAllTextAsync(Path.Combine(evidence, "observer-compile.stdout.log"), result.Output, token);
        await File.WriteAllTextAsync(Path.Combine(evidence, "observer-compile.stderr.log"), result.Error, token);
        Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
        return new(executable, scratch);
    }

    public async Task<JsonElement> InspectAsync(WindowsProcessIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); identity.Validate();
        string request = Path.Combine(scratch, "ui-request-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
            { schemaVersion = 1, identity.ProcessId, identity.CreationFileTime, identity.Executable },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await WindowsLauncherTests.Invoke(executable, [request], scratch, deadline.Token, input: null);
            if (result.ExitCode != 0) throw new InvalidDataException("The native UI observation failed: " + result.Error);
            using var document = JsonDocument.Parse(result.Output);
            Assert.AreEqual("observed", document.RootElement.GetProperty("status").GetString());
            Assert.IsFalse(document.RootElement.GetProperty("nativeMutation").GetBoolean());
            return document.RootElement.Clone();
        }
        finally { File.Delete(request); }
    }
}
