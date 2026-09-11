using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Explicit protocol fixtures, not evidence of a running KiCad process.
[TestClass]
public sealed class NativeClientTests
{
    [TestMethod]
    public async Task HandshakePinsEpochOnSubsequentCalls()
    {
        var fixture = new FixtureTransport();
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        await client.HandshakeAsync();
        await client.GetVersionAsync();
        Assert.AreEqual("fixture-epoch", fixture.LastRequest!.Header.KicadToken);
        Assert.AreEqual("fixture-epoch", client.Epoch);
    }

    [TestMethod]
    public async Task RecycledSocketCannotBecomeAnotherInstance()
    {
        var fixture = new FixtureTransport();
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        await client.HandshakeAsync();
        fixture.Epoch = "replacement-process";
        AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() => client.GetVersionAsync());
        Assert.AreEqual("instance_changed", error.Code);
    }

    [TestMethod]
    public async Task NativeFailureIsNotASuccessfulEmptyResponse()
    {
        var fixture = new FixtureTransport { Status = 7 };
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        NativeApiException error = await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.HandshakeAsync());
        Assert.AreEqual(7, error.Status);
    }

    [TestMethod]
    public async Task WrongResponseTypeIsRejected()
    {
        var fixture = new FixtureTransport { WrongType = true };
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() => client.HandshakeAsync());
        Assert.AreEqual("invalid_response", error.Code);
    }

    [TestMethod]
    public void RemoteOrImplicitEndpointsAreRejected()
    {
        foreach (string endpoint in new[] { "tcp://localhost:9000", "ipc://relative", "", "ipc:///tmp/a\0b" })
            Assert.ThrowsExactly<ArgumentException>(() => NngTransport.ValidateEndpoint(endpoint));
        NngTransport.ValidateEndpoint("ipc:///tmp/a.sock");
    }

    [TestMethod]
    public void SchematicOpenRequiresAnExplicitNativePath()
    {
        var fixture = new FixtureTransport();
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        foreach (string path in new[] { "relative.kicad_sch", "/tmp/legacy.sch", "" })
        {
            Assert.ThrowsExactly<AutomationException>(() => client.OpenRootSchematicAsync(path));
            Assert.ThrowsExactly<AutomationException>(() => client.CreateRootSchematicAsync(path));
        }
        Assert.IsNull(fixture.LastRequest, "Invalid paths must fail before transport.");
    }

    [TestMethod]
    public async Task SnapshotRequestsOptIntoCurrentFieldsWithoutChangingCallerMessages()
    {
        IMessage[] requests = [new ReadSchematicMetadata(), new ReadSchematicScreenData(),
            new ReadSchematicHierarchyData(), new ReadSchematicElectricalState(), new CaptureSchematicObservation(), new RenderSchematicViews()];
        foreach (var request in requests)
        {
            var field = request.Descriptor.FindFieldByName("schema_version");
            var current = NativeClient.CurrentSnapshotRequest(request);
            Assert.AreNotSame(request, current);
            Assert.AreEqual(0U, field.Accessor.GetValue(request));
            Assert.AreEqual(2U, field.Accessor.GetValue(current));
            foreach (uint explicitVersion in new[] { 1U, 2U, 3U })
            {
                field.Accessor.SetValue(request, explicitVersion);
                Assert.AreSame(request, NativeClient.CurrentSnapshotRequest(request));
                Assert.AreEqual(explicitVersion, field.Accessor.GetValue(request));
            }
        }
        var other = new ReadSchematicSaveState();
        Assert.AreSame(other, NativeClient.CurrentSnapshotRequest(other));
        var transport = new FixtureTransport();
        var client = new NativeClient(transport, "ipc:///tmp/schema-fixture.sock");
        var query = new ReadSchematicMetadata();
        // The transport is deliberately a protocol fixture, not a native peer.
        await client.InvokeAsync<ReadSchematicMetadata, GetVersionResponse>(query);
        Assert.AreEqual(2U, transport.LastRequest!.Message.Unpack<ReadSchematicMetadata>().SchemaVersion);
        Assert.AreEqual(0U, query.SchemaVersion);
    }

    internal sealed class FixtureTransport : INativeTransport
    {
        public string Epoch { get; set; } = "fixture-epoch";
        public int Status { get; set; } = 1;
        public bool WrongType { get; set; }
        public string InstanceId { get; } = Guid.NewGuid().ToString("D");
        public string ProjectPath { get; set; } = "/fixture/test.kicad_pro";
        public ApiRequest? LastRequest { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            LastRequest = ApiRequest.Parser.ParseFrom(request);
            IMessage payload = LastRequest.Message.Is(GetAutomationSession.Descriptor) && !WrongType
                ? new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId, ProjectPath = ProjectPath, Epoch = Epoch }
                : new GetVersionResponse { Version = new Kiapi.Common.Types.KiCadVersion { FullVersion = "isolated-protocol-fixture" } };
            return Task.FromResult(new ApiResponse
            {
                Header = new ApiResponseHeader { KicadToken = Epoch },
                Status = new ApiResponseStatus { Status = (ApiStatusCode)Status, ErrorMessage = Status == 1 ? "" : "fixture busy" },
                Message = Any.Pack(payload)
            }.ToByteArray());
        }
    }
}
