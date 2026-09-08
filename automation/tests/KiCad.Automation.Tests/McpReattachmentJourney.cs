using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMcpReattachment(ProcessStartInfo start, string instanceId,
        DocumentSpecifier document, SchematicScreenDataSnapshot expected, SchematicSaveState dirty,
        CancellationToken token)
    {
        using Process process = Process.Start(start)!;
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(token);
        int nextId = 0;
        try
        {
            await Request("initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "native-reattachment-journey", version = "1" }
            });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var saved = await Call("kicad_instance_saved_sessions", new { });
            using var records = JsonDocument.Parse(saved.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual(instanceId, records.RootElement[0].GetProperty("instanceId").GetString());
            await Call("kicad_instance_reattach", new { instanceId });
            string documentJson = JsonFormatter.Default.Format(document);
            var state = await Call("kicad_schematic_save_state", new { instanceId, documentJson });
            Assert.AreEqual(dirty, SchematicJson.Parser.Parse<SchematicSaveState>(state.GetProperty("structuredContent").GetProperty("state").GetRawText()));
            var snapshot = await Call("kicad_schematic_data", new { instanceId, documentJson });
            Assert.AreEqual(expected, SchematicJson.Parser.Parse<SchematicScreenDataSnapshot>(snapshot.GetProperty("structuredContent").GetProperty("snapshot").GetRawText()),
                "Reconnecting must preserve unsaved in-memory objects and their revision, not reload disk contents.");
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.AreEqual(0, process.ExitCode, await diagnostics);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }

        async Task<JsonElement> Call(string name, object arguments)
        {
            var response = await Request("tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
            return result;
        }

        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                Assert.IsNotNull(line, "Restarted MCP exited before responding.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    return response.RootElement.Clone();
            }
        }
    }
}
