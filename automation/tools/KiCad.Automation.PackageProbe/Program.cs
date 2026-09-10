using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;

if (args.Length == 2 && args[0] == "--nng-binding-lifetime")
    return await NngBindingLifetimeProbe.RunAsync(args[1]);
if (args.Length == 3 && args[0] == "--nng-binding-lifetime" && args[2] == "--reopen-path")
    return await NngBindingLifetimeProbe.RunAsync(args[1], reopenPath: true);

if (args.Length == 2 && args[0] == "--verify")
{
    using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(args[1], "result.json")));
    if (result.RootElement.GetProperty("status").GetString() != "passed"
        || result.RootElement.GetProperty("user").GetString() != "probe")
        throw new InvalidDataException("The isolated non-root package journey did not pass.");
    foreach (int round in new[] { 0, 1 })
    {
        byte[] png = await File.ReadAllBytesAsync(Path.Combine(args[1], "render-" + round + ".png"));
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Missing native render evidence.");
    }
    Console.WriteLine("Isolated non-root package journey evidence verified; not cross-platform readiness.");
    return 0;
}
if (args.Length != 2 || !args.All(Path.IsPathFullyQualified))
    throw new ArgumentException("Provide absolute installed-bundle and evidence paths.");
string bundle = args[0], evidence = Directory.CreateDirectory(args[1]).FullName;
string temporary = Directory.CreateTempSubdirectory("kicad-clean-probe-").FullName;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var processes = new List<Process>();
var captures = new List<Task>();
try
{
    if (Environment.UserName != "probe")
        throw new InvalidDataException("Run this clean-host probe as the isolated non-root probe user.");
    NativeLibrary.SetDllImportResolver(typeof(NngTransport).Assembly, (name, _, _) =>
        name == "nng" ? NativeLibrary.Load(Path.Combine(bundle, "runtime/lib/kicad-automation/libnng.so")) : IntPtr.Zero);
    var displayStart = new ProcessStartInfo("Xvfb")
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in new[] { "-displayfd", "1", "-screen", "0", "1280x900x24", "-nolisten", "tcp" })
        displayStart.ArgumentList.Add(arg);
    var display = Process.Start(displayStart)!;
    processes.Add(display);
    captures.Add(Capture(display.StandardError, "xvfb.stderr.log"));
    string? displayNumber = await display.StandardOutput.ReadLineAsync(deadline.Token);
    if (!int.TryParse(displayNumber, out _)) throw new InvalidDataException("No X display was allocated.");
    string project = Path.Combine(temporary, "fixture.kicad_pro");
    string schematic = Path.Combine(temporary, "fixture.kicad_sch");
    await File.WriteAllTextAsync(project, "{\"meta\":{\"version\":3}}", deadline.Token);
    string instanceId = Guid.NewGuid().ToString("D");
    string socket = Path.Combine(temporary, "api.sock");
    var nativeStart = new ProcessStartInfo(Path.Combine(bundle, "kicad-codex"))
    { WorkingDirectory = temporary, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    nativeStart.Environment["DISPLAY"] = ":" + displayNumber;
    nativeStart.Environment["KICAD_CONFIG_HOME"] = Path.Combine(temporary, "config");
    nativeStart.Environment["KICAD_CACHE_HOME"] = Path.Combine(temporary, "cache");
    foreach (string arg in new[] { "--new", "--automation", instanceId, "--api-socket", socket,
        "--automation-log", Path.Combine(evidence, "native.log"), "--software-rendering", project })
        nativeStart.ArgumentList.Add(arg);
    var native = Process.Start(nativeStart)!;
    processes.Add(native);
    captures.Add(Capture(native.StandardOutput, "native.stdout.log"));
    captures.Add(Capture(native.StandardError, "native.stderr.log"));
    var client = new NativeClient(new NngTransport(), "ipc://" + socket);
    while (true)
    {
        deadline.Token.ThrowIfCancellationRequested();
        if (native.HasExited) throw new InvalidDataException("Installed KiCad exited before readiness.");
        try
        {
            var handshake = await client.HandshakeAsync(deadline.Token);
            if (handshake.InstanceId != instanceId) throw new InvalidDataException("Wrong native instance.");
            break;
        }
        catch (NngException) { }
        catch (NativeApiException error) when (error.Status is 4 or 7) { }
        await Task.Delay(100, deadline.Token);
    }
    DocumentSpecifier? document = null;
    for (int round = 0; round < 2; round++)
    {
        var start = new ProcessStartInfo(Path.Combine(bundle, "kicad-mcp"))
        {
            WorkingDirectory = temporary, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = Path.Combine(temporary, "registry");
        using var mcp = Process.Start(start)!;
        var log = Capture(mcp.StandardError, "mcp-" + round + ".stderr.log");
        int nextId = 0;
        try
        {
            await Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "clean-package-probe", version = "1" } });
            await mcp.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await Tool("kicad_instance_attach", new { endpoint = "ipc://" + socket, expectedInstanceId = instanceId });
            JsonElement created = await Tool("kicad_schematic_create", new { instanceId, path = schematic });
            string documentJson = created.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
            var current = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
            if (document is not null && !current.Equals(document))
                throw new InvalidDataException("MCP reconnect changed the document identity.");
            document = current;
            JsonElement observed;
            while (true)
            {
                observed = await RawTool("kicad_schematic_observe", new { instanceId, documentJson });
                if (!IsError(observed)) break;
                string text = observed.GetProperty("content")[0].GetProperty("text").GetString()!;
                using var error = JsonDocument.Parse(text);
                if (error.RootElement.GetProperty("code").GetString() is not ("native_status_4" or "native_status_7"))
                    throw new InvalidDataException("Observation failed: " + text);
                await Task.Delay(100, deadline.Token);
            }
            byte[] png = Convert.FromBase64String(observed.GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "image").GetProperty("data").GetString()!);
            if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("No native PNG was returned through MCP.");
            await File.WriteAllBytesAsync(Path.Combine(evidence, "render-" + round + ".png"), png, deadline.Token);
        }
        finally
        {
            mcp.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await mcp.WaitForExitAsync(exit.Token); }
            finally
            {
                if (!mcp.HasExited) { mcp.Kill(entireProcessTree: true); await mcp.WaitForExitAsync(); }
                await log;
            }
        }
        if (native.HasExited) throw new InvalidDataException("MCP exit terminated the native editor.");

        async Task<JsonElement> Tool(string name, object arguments)
        {
            var result = await RawTool(name, arguments);
            if (IsError(result)) throw new InvalidDataException(name + ": " + result.GetRawText());
            return result;
        }
        async Task<JsonElement> RawTool(string name, object arguments) =>
            (await Request("tools/call", new { name, arguments })).GetProperty("result").Clone();
        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await mcp.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await mcp.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null) throw new InvalidDataException("MCP exited before replying.");
                using var response = JsonDocument.Parse(line);
                if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (response.RootElement.TryGetProperty("error", out _))
                    throw new InvalidDataException(response.RootElement.GetRawText());
                return response.RootElement.Clone();
            }
        }
    }
    await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document! }, deadline.Token);
    if (!File.Exists(schematic)) throw new InvalidDataException("Native save did not create the schematic.");
    await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
    { status = "passed", user = Environment.UserName, instanceId, nativeVersion = (await client.GetVersionAsync(deadline.Token)).Version.FullVersion,
        behavior = "MCP create/render/reconnect, dirty-session survival, native save", crossPlatformReady = false }), deadline.Token);
    Console.WriteLine("Clean-package native/MCP journey passed.");
    return 0;
}
catch (Exception error)
{
    await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
    { status = "failed", error = error.ToString(), crossPlatformReady = false }));
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally
{
    foreach (var process in processes.AsEnumerable().Reverse())
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
    }
    await Task.WhenAll(captures);
    Directory.Delete(temporary, recursive: true);
}

static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var error) && error.GetBoolean();
async Task Capture(StreamReader input, string name)
{
    await using var output = new StreamWriter(Path.Combine(evidence, name));
    string? line;
    while ((line = await input.ReadLineAsync()) is not null) await output.WriteLineAsync(line);
}
