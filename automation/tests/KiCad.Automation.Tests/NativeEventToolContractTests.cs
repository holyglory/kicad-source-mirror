using System.Reflection;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeEventToolContractTests
{
    [TestMethod]
    public void EventWaitAdvertisesStructuredOutput()
    {
        var method = typeof(EventTools).GetMethod(nameof(EventTools.Wait))!;
        var contract = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.AreEqual("kicad_events_wait", contract.Name);
        Assert.IsTrue(contract.UseStructuredContent,
            "Consumers resume from typed notification identities; text-only output breaks that contract.");
        Assert.AreEqual(typeof(NativeEventWaitResult), contract.OutputSchemaType);
        Assert.AreEqual(typeof(Task<CallToolResult>), method.ReturnType);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(61)]
    public async Task InvalidDeadlineHasStructuredFailureBeforeAccessingNativeState(int timeout)
    {
        // Invalid arguments must be rejected before any registry or transport access.
        var result = await new EventTools(null!).Wait("fixture", CancellationToken.None, timeoutSeconds: timeout);
        Assert.IsTrue(result.IsError == true);
        var structured = result.StructuredContent!.Value;
        Assert.AreEqual("Failed", structured.GetProperty("status").GetString());
        Assert.AreEqual("invalid_deadline", structured.GetProperty("errorCode").GetString());
        Assert.IsFalse(structured.GetProperty("trackingComplete").GetBoolean());
    }

    [TestMethod]
    public async Task IncompleteResumeIdentityHasStructuredFailure()
    {
        var result = await new EventTools(null!).Wait("fixture", CancellationToken.None, afterSequence: 0);
        Assert.IsTrue(result.IsError == true);
        Assert.AreEqual("missing_event_epoch", result.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [TestMethod]
    public async Task CancellationIsNotConvertedToAToolFailure()
    {
        var waiting = new EventTools(null!).Wait("fixture", new CancellationToken(canceled: true));
        try { await waiting; Assert.Fail("A cancelled observation returned a result."); }
        catch (OperationCanceledException) { Assert.IsTrue(waiting.IsCanceled); }
    }

    [TestMethod]
    [DataRow(true, "native_status_7")]
    [DataRow(false, "transport_status_5")]
    public async Task NativeFailuresHaveTypedDetailsAndPreserveTheAttachedSession(bool nativeStatus, string code)
    {
        string state = Directory.CreateTempSubdirectory("kicad-event-errors-").FullName;
        try
        {
            var transport = new FaultingEventTransport();
            var registry = new InstanceRegistry(transport, state);
            var tools = new EventTools(registry);
            string id = transport.Native.InstanceId;
            var unknown = await tools.Wait(id, CancellationToken.None);
            Assert.AreEqual("unknown_instance", unknown.StructuredContent!.Value.GetProperty("errorCode").GetString());
            Assert.IsNull(transport.Native.LastRequest, "An unknown target must not contact a native peer.");
            await registry.AttachAsync("ipc:///tmp/event-error-fixture.sock", id);
            var attached = registry.Get(id);
            if (nativeStatus) transport.Native.Status = 7;
            else transport.Failure = new NngException(5, "Isolated transport failure fixture");
            var result = await tools.Wait(id, CancellationToken.None);
            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual(code, result.StructuredContent!.Value.GetProperty("errorCode").GetString());
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null,
                result.StructuredContent.Value.GetProperty("notification").ValueKind);
            transport.Native.Status = 1; transport.Failure = null;
            Assert.AreEqual(attached.Epoch, (await registry.Client(id).HandshakeAsync()).Epoch);
            Assert.AreEqual(attached, registry.Get(id));
        }
        finally { Directory.Delete(state, true); }
    }

    private sealed class FaultingEventTransport : INativeTransport
    {
        public NativeClientTests.FixtureTransport Native { get; } = new();
        public Exception? Failure { get; set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default) => Failure is { } failure
                ? Task.FromException<byte[]>(failure)
                : Native.ExchangeAsync(endpoint, request, timeout, cancellationToken);
    }
}
