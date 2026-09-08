using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ConnectedMoveToolTests
{
    private static readonly string SymbolId = Guid.NewGuid().ToString("D");
    private static readonly string DocumentJson = SchematicJson.Formatter.Format(new DocumentSpecifier
    {
        Type = (DocumentType)1,
        SheetPath = new() { Path = { new KIID { Value = Guid.NewGuid().ToString("D") } } }
    });

    [TestMethod]
    public void ContractIsAnExplicitStructuredMutation()
    {
        var contract = typeof(SchematicMutationTools).GetMethod(nameof(SchematicMutationTools.MoveConnectedSymbols))!
            .GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.AreEqual("kicad_schematic_move_connected_symbols", contract.Name);
        Assert.IsTrue(contract.UseStructuredContent);
        Assert.AreEqual(typeof(ConnectedMoveToolResult), contract.OutputSchemaType);
        Assert.IsFalse(contract.ReadOnly);
    }

    [TestMethod]
    public async Task InvalidRequestsNeverAccessRegistry()
    {
        var tools = new SchematicMutationTools(null!);
        foreach (var (ids, x, epoch, operation) in new (string[], long, string, string)[]
        {
            ([], 100, "epoch", "op"), ([SymbolId, SymbolId], 100, "epoch", "op"),
            (["not-a-uuid"], 100, "epoch", "op"), ([SymbolId], 1, "epoch", "op"),
            ([SymbolId], long.MaxValue, "epoch", "op"), ([SymbolId], 100, "", "op"),
            ([SymbolId], 100, "epoch", ""), ([SymbolId], 100, "epoch", "a\0b")
        })
        {
            var result = await tools.MoveConnectedSymbols("instance", DocumentJson, ids, x, 100,
                epoch, 3, operation, CancellationToken.None);
            Assert.IsTrue(result.IsError == true);
            Assert.AreEqual("Rejected", result.StructuredContent!.Value.GetProperty("status").GetString());
        }
        foreach (string document in new[] { "{}", "invalid" })
            Assert.IsTrue((await tools.MoveConnectedSymbols("instance", document, [SymbolId], 100, 100,
                "epoch", 3, "op", CancellationToken.None)).IsError == true);
    }

    [TestMethod]
    public async Task PreCancelledRequestsDoNotReachNativeState()
    {
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new SchematicMutationTools(null!)
            .MoveConnectedSymbols("instance", DocumentJson, [SymbolId], 100, 100, "epoch", 3, "op", new(true)));
    }

    [TestMethod]
    public async Task TypedRequestPreservesRetryIdentityAndDoesNotInventRollbackAfterTransportLoss()
    {
        string state = Directory.CreateTempSubdirectory("kicad-move-tool-").FullName;
        try
        {
            var transport = new MoveTransport();
            var registry = new InstanceRegistry(transport, state);
            await registry.AttachAsync("ipc:///tmp/move-tool-fixture.sock", transport.Identity.InstanceId);
            var tools = new SchematicMutationTools(registry);
            var first = await tools.MoveConnectedSymbols(transport.Identity.InstanceId, DocumentJson,
                [SymbolId], 100, -200, "document-epoch", 42, "same-operation", CancellationToken.None);
            Assert.AreEqual("Completed", first.StructuredContent!.Value.GetProperty("status").GetString());
            Assert.IsFalse(first.StructuredContent.Value.GetProperty("trackingComplete").GetBoolean());
            var request = transport.Request!.Clone();
            Assert.AreEqual("same-operation", request.OperationId);
            Assert.AreEqual("document-epoch", request.DocumentEpoch);
            Assert.AreEqual(42UL, request.ExpectedRevision.Sequence);
            Assert.AreEqual("document-epoch", request.ExpectedRevision.Epoch);
            Assert.AreEqual(SymbolId, request.Operations.Single().MoveConnectedSymbols.Symbols.Single().Value);
            Assert.AreEqual(-200L, request.Operations.Single().MoveConnectedSymbols.Delta.YNm);
            transport.LoseReply = true;
            var lost = await tools.MoveConnectedSymbols(transport.Identity.InstanceId, DocumentJson,
                [SymbolId], 100, -200, "document-epoch", 42, "same-operation", CancellationToken.None);
            Assert.AreEqual(request, transport.Request);
            Assert.AreEqual("NotConfirmed", lost.StructuredContent!.Value.GetProperty("status").GetString());
            Assert.IsTrue(lost.IsError == true);
            transport.LoseReply = false;
            var recovered = await tools.MoveConnectedSymbols(transport.Identity.InstanceId, DocumentJson,
                [SymbolId], 100, -200, "document-epoch", 42, "same-operation", CancellationToken.None);
            Assert.IsFalse(recovered.IsError == true);
            Assert.AreEqual(request, transport.Request);
        }
        finally { Directory.Delete(state, recursive: true); }
    }

    // This verifies the MCP/native contract only; the separate real editor
    // journey proves that an actual connected move changes the design.
    private sealed class MoveTransport : INativeTransport
    {
        internal readonly NativeClientTests.FixtureTransport Identity = new();
        internal ApplySchematicItemBatch? Request;
        internal bool LoseReply;
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var envelope = ApiRequest.Parser.ParseFrom(request);
            if (!envelope.Message.Is(ApplySchematicItemBatch.Descriptor))
                return Identity.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            Request = envelope.Message.Unpack<ApplySchematicItemBatch>();
            if (LoseReply) throw new NngException(5, "Test fixture lost the reply after accepting the request.");
            return Task.FromResult(new ApiResponse
            {
                Header = new() { KicadToken = Identity.Epoch }, Status = new() { Status = (ApiStatusCode)1 },
                Message = Any.Pack(new SchematicItemBatchResult())
            }.ToByteArray());
        }
    }
}
