using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsCaptionActionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeCaptionPreservesVisibilityOwnershipAccessibilityAndMouseInteraction()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows caption rendering and input."); return; }
        string root = Directory.CreateTempSubdirectory("kwcaption-").FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("KICAD_HOSTED_FIXTURE_EVIDENCE")
            ?? TestContext.TestResultsDirectory!, "windows-caption-action")).FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var job = new WindowsProcessJob();
        Process? process = null;
        Task<string>? stderr = null;
        try
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "libs/kiplatform/port/wxmsw/caption_action.cpp"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            string executable = Path.Combine(root, "caption.exe");
            var compiled = await WindowsLauncherTests.Invoke("cl.exe", ["/nologo", "/EHsc", "/std:c++20", "/MT", "/utf-8", "/DUNICODE", "/D_UNICODE",
                "/I" + Path.Combine(repository.FullName, "thirdparty/nlohmann_json"),
                "/I" + Path.Combine(repository.FullName, "libs/kiplatform/port/wxmsw"),
                Path.Combine(repository.FullName, "libs/kiplatform/port/wxmsw/caption_action.cpp"),
                Path.Combine(repository.FullName, "automation/tests/fixtures/windows-caption-action.cpp"),
                "/Fe:" + executable, "user32.lib", "gdi32.lib", "comctl32.lib", "oleacc.lib", "oleaut32.lib", "ole32.lib", "uuid.lib", "dwmapi.lib"], root, deadline.Token, input: null);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stdout.log"), compiled.Output, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(evidence, "compile.stderr.log"), compiled.Error, deadline.Token);
            Assert.AreEqual(0, compiled.ExitCode, compiled.Output + compiled.Error);
            var observer = await WindowsUiObserver.CreateAsync(root, evidence, deadline.Token);
            process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 })!;
            job.Attach(process); stderr = process.StandardError.ReadToEndAsync();
            var state = await Read();
            nint first = await WindowsNativeUi.WaitForWindow(process, "KiCad caption fixture 0", deadline.Token);
            nint second = await WindowsNativeUi.WaitForWindow(process, "KiCad caption fixture 1", deadline.Token);
            Assert.IsTrue(Invisible(state, 0)); Assert.IsTrue(Unavailable(state, 0));
            Assert.AreEqual(0, Frame(state, 0)["menuActions"]!.GetValue<int>());
            state = await Command(new { op = "invoke" }); Assert.IsTrue(state["invokeResult"]!.GetValue<int>() < 0);
            state = await Command(new { op = "set", visible = true, enabled = true });
            Assert.IsFalse(Invisible(state, 0)); Assert.IsFalse(Unavailable(state, 0));
            Assert.AreEqual("Update", Frame(state, 0)["label"]!.GetValue<string>());
            Assert.AreEqual(0x2b, Frame(state, 0)["role"]!.GetValue<int>()); // Native MSAA push button.
            Assert.AreEqual(1, Frame(state, 0)["menuActions"]!.GetValue<int>());
            WindowsNativeUi.Capture(process, first, Path.Combine(evidence, "available.png"));
            var identity = KiCad.Automation.Mcp.WindowsProcessIdentity.Read(process.Id);
            var observed = await observer.InspectAsync(identity, deadline.Token);
            var externalButton = observed.GetProperty("buttons").EnumerateArray().Single(button =>
                button.GetProperty("title").GetString() == "Update" && button.GetProperty("visible").GetBoolean());
            Assert.IsTrue(externalButton.GetProperty("enabled").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(evidence, "external-observation.json"), observed.GetRawText(), deadline.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => observer.InspectAsync(identity with { CreationFileTime = "1" }, deadline.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => observer.InspectAsync(identity, new CancellationToken(true)));
            Assert.IsFalse(process.HasExited);
            var ui = new WindowsUiAutomation(observer);
            foreach (string choice in new[] { "Cancel", "Save" })
            {
                await Send(new { op = "dialog" });
                await ui.WaitButtonAsync(identity, choice, deadline.Token);
                var modal = await observer.InspectAsync(identity, deadline.Token);
                Assert.IsFalse(modal.GetProperty("buttons").EnumerateArray().Any(button =>
                    button.GetProperty("title").GetString() == "Update" && button.GetProperty("enabled").GetBoolean()));
                var dialogButton = modal.GetProperty("buttons").EnumerateArray().Single(button =>
                    button.GetProperty("title").GetString() == choice && button.GetProperty("enabled").GetBoolean());
                nint dialog = checked((nint)ulong.Parse(dialogButton.GetProperty("window").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                WindowsNativeUi.Capture(process, dialog, Path.Combine(evidence, choice.ToLowerInvariant() + "-dialog.png"));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ui.PressAsync(identity, "Update", deadline.Token));
                await ui.PressAsync(identity, choice, deadline.Token);
                state = await Read(); Assert.AreEqual(choice == "Save" ? 6 : 2, state["dialogResult"]!.GetValue<int>());
                Assert.IsFalse(process.HasExited);
                Assert.IsFalse(Unavailable(state, 0));
            }
            using (var wrongOwner = Process.GetCurrentProcess())
                Assert.ThrowsExactly<InvalidOperationException>(() => WindowsNativeUi.Click(wrongOwner, first, 0, 0));
            Click(state, 0, first);
            state = await WaitClicks(1);
            Assert.AreEqual(0, Frame(state, 1)["clicks"]!.GetValue<int>());
            WindowsNativeUi.Capture(process, first, Path.Combine(evidence, "clicked.png"));
            Click(state, 0, first, cancel: true);
            for (int attempt = 0; attempt < 30; ++attempt)
            {
                state = await Command(new { op = "state" });
                if (Frame(state, 0)["escapes"]!.GetValue<int>() > 0) break;
                await Task.Delay(50, deadline.Token);
            }
            Assert.AreEqual(1, Frame(state, 0)["escapes"]!.GetValue<int>(), "The cancellation must reach the actual input queue.");
            Assert.AreEqual(1, Frame(state, 0)["clicks"]!.GetValue<int>());

            state = await Command(new { op = "set", visible = true, enabled = false });
            Assert.IsTrue(Unavailable(state, 0)); Click(state, 0, first);
            WindowsNativeUi.Capture(process, first, Path.Combine(evidence, "disabled.png"));
            state = await Command(new { op = "invoke" }); Assert.IsTrue(state["invokeResult"]!.GetValue<int>() < 0);
            Assert.AreEqual(1, Frame(state, 0)["clicks"]!.GetValue<int>());
            state = await Command(new { op = "set", visible = false, enabled = true });
            Assert.IsTrue(Invisible(state, 0)); Assert.IsTrue(Unavailable(state, 0));
            Assert.AreEqual(0, Frame(state, 0)["menuActions"]!.GetValue<int>());
            WindowsNativeUi.Capture(process, first, Path.Combine(evidence, "hidden.png"));
            state = await Command(new { op = "invoke" }); Assert.IsTrue(state["invokeResult"]!.GetValue<int>() < 0);
            state = await Command(new { op = "set", visible = true, enabled = true });
            state = await Command(new { op = "resize", width = 160 }); Assert.IsTrue(Invisible(state, 0));
            WindowsNativeUi.Capture(process, first, Path.Combine(evidence, "narrow.png"));
            state = await Command(new { op = "invoke" }); Assert.IsTrue(state["invokeResult"]!.GetValue<int>() < 0);
            state = await Command(new { op = "resize", width = 760 }); Assert.IsFalse(Invisible(state, 0));
            WindowsNativeUi.Capture(process, first, Path.Combine(evidence, "restored-width.png"));
            state = await Command(new { op = "enable", enabled = false });
            Assert.IsTrue(Unavailable(state, 0));
            state = await Command(new { op = "invoke" }); Assert.IsTrue(state["invokeResult"]!.GetValue<int>() < 0);
            state = await Command(new { op = "enable", enabled = true }); Assert.IsFalse(Unavailable(state, 0));
            Assert.IsTrue((await Command(new { op = "duplicate" }))["duplicateRefused"]!.GetValue<bool>());
            Assert.IsTrue((await Command(new { op = "wrong-thread" }))["wrongThreadRefused"]!.GetValue<bool>());
            state = await Command(new { op = "show", visible = false }); Assert.IsTrue(Invisible(state, 0));
            state = await Command(new { op = "invoke" }); Assert.IsTrue(state["invokeResult"]!.GetValue<int>() < 0);
            state = await Command(new { op = "show", visible = true });
            state = await Command(new { op = "invoke" }); Assert.AreEqual(0, state["invokeResult"]!.GetValue<int>());
            Assert.AreEqual(2, Frame(state, 0)["clicks"]!.GetValue<int>());
            state = await Command(new { op = "destroy" });
            Assert.IsFalse(Frame(state, 0)["alive"]!.GetValue<bool>());
            Assert.IsTrue(state["staleInvokeResult"]!.GetValue<int>() < 0);
            state = await Command(new { op = "set", index = 1, visible = true, enabled = true });
            Click(state, 1, second);
            for (int attempt = 0; attempt < 30; ++attempt)
            {
                state = await Command(new { op = "state" });
                if (Frame(state, 1)["clicks"]!.GetValue<int>() == 1) break;
                await Task.Delay(50, deadline.Token);
            }
            Assert.AreEqual(1, Frame(state, 1)["clicks"]!.GetValue<int>());
            WindowsNativeUi.Capture(process, second, Path.Combine(evidence, "independent-window.png"));
            // Keyboard remains required. Run independent failure/recovery
            // checks first so a menu failure does not hide their findings.
            WindowsNativeUi.Shortcut(process, second, 0x12, 0x20); // Alt+Space
            await WindowsNativeUi.SelectSystemMenuItem(process, second, "Update", deadline.Token);
            WindowsNativeUi.PressKey(process, second, 0x0d); // Enter
            state = await WaitClicks(2, index: 1);
            await File.WriteAllTextAsync(Path.Combine(evidence, "final-state.json"), state.ToJsonString(), deadline.Token);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token); Assert.AreEqual(0, process.ExitCode);
            string receipt = Path.Combine(evidence, "result.json");
            await File.WriteAllTextAsync(receipt, "{\"schemaVersion\":1,\"status\":\"passed\",\"nativeMouseClick\":true,\"nativeKeyboardMenu\":true,\"accessibleAction\":true,\"hiddenAndDisabledRefused\":true,\"independentOwners\":true,\"destroyedOwnerRefused\":true,\"actualKiCadUpdateJourney\":false}", deadline.Token);
            TestContext.AddResultFile(receipt);

            async Task<JsonObject> Read()
            {
                string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null) Assert.Fail(await stderr);
                await File.AppendAllTextAsync(Path.Combine(evidence, "states.jsonl"), line + "\n", deadline.Token);
                var result = JsonNode.Parse(line!)!.AsObject(); Assert.AreEqual("ok", result["status"]!.GetValue<string>(), result.ToJsonString()); return result;
            }
            async Task<JsonObject> Command(object command)
            {
                await Send(command); return await Read();
            }
            async Task Send(object command)
            {
                await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(command));
                await process.StandardInput.FlushAsync(deadline.Token);
            }
            async Task<JsonObject> WaitClicks(int expected, int index = 0)
            {
                for (int attempt = 0; attempt < 30; ++attempt)
                {
                    var result = await Command(new { op = "state" });
                    if (Frame(result, index)["clicks"]!.GetValue<int>() == expected) return result;
                    await Task.Delay(50, deadline.Token);
                }
                throw new AssertFailedException($"The native action did not reach window {index}; expected {expected} callbacks.");
            }
            void Click(JsonObject result, int index, nint window, bool cancel = false)
            {
                var rectangle = Frame(result, index)["rectangle"]!;
                Assert.IsTrue(rectangle["width"]!.GetValue<int>() > 0);
                WindowsNativeUi.Click(process, window, rectangle["x"]!.GetValue<int>() + rectangle["width"]!.GetValue<int>() / 2,
                    rectangle["y"]!.GetValue<int>() + rectangle["height"]!.GetValue<int>() / 2, cancel);
            }
        }
        finally
        {
            if (process is not null) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); }
            if (stderr is not null) await File.WriteAllTextAsync(Path.Combine(evidence, "fixture.stderr.log"), await stderr);
            foreach (string file in Directory.GetFiles(evidence, "*.png")) TestContext.AddResultFile(file);
            await WindowsFixtureCleanup.RemoveOwnedTemporaryDirectoryAsync(root);
        }
    }
    private static JsonNode Frame(JsonObject value, int index) => value["frames"]![index]!;
    private static bool Invisible(JsonObject value, int index) => (Frame(value, index)["state"]!.GetValue<int>() & 0x8000) != 0;
    private static bool Unavailable(JsonObject value, int index) => (Frame(value, index)["state"]!.GetValue<int>() & 1) != 0;
}
