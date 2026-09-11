using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySnapshotSchemaVersions(NativeClient client, DocumentSpecifier document, CancellationToken token)
    {
        const string gap = "erc_settings_require_snapshot_schema_2";
        void Legacy(SchematicMetadata metadata)
        {
            Assert.IsNull(metadata.ErcSettings);
            CollectionAssert.Contains(metadata.UnrepresentedState.ToArray(), gap);
        }
        var current = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token);
        Assert.IsNotNull(current.Data.Metadata.ErcSettings);
        var legacy = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 1 }, token);
        Legacy(legacy.Data.Metadata);
        var expected = current.Clone(); expected.Data.Metadata.ErcSettings = null; expected.Data.Metadata.UnrepresentedState.Add(gap);
        Assert.AreEqual(expected, legacy, "Legacy projection may omit only the explicitly declared new fields.");

        // A truly old sender omits the new request field. Bypass the current
        // client's automatic opt-in to exercise those exact legacy wire bytes.
        var oldRequest = new ApiRequest { Header = new() { KicadToken = client.Epoch, ClientName = "legacy-snapshot-fixture" },
            Message = Any.Pack(new ReadSchematicScreenData { Document = document }) };
        var oldReply = ApiResponse.Parser.ParseFrom(await new NngTransport().ExchangeAsync(client.Endpoint,
            oldRequest.ToByteArray(), TimeSpan.FromSeconds(15), token));
        Assert.AreEqual(1, (int)oldReply.Status.Status);
        Assert.AreEqual(client.Epoch, oldReply.Header.KicadToken);
        Assert.AreEqual(legacy, oldReply.Message.Unpack<SchematicScreenDataSnapshot>());

        var metadata = await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document, SchemaVersion = 1 }, token);
        Legacy(metadata.Metadata);
        var hierarchy = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document, SchemaVersion = 1 }, token);
        foreach (var instance in hierarchy.Data.Instances) Legacy(instance.Metadata);
        var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document, SchemaVersion = 1 }, token);
        Legacy(observation.Snapshot.Data.Metadata);
        Assert.AreEqual(observation.Snapshot.Revision, observation.Preview.Revision);
        Assert.IsTrue(observation.Preview.Png.Length > 24);

        var views = new RenderSchematicViews { Document = document, SchemaVersion = 1 };
        views.Views.Add(new SchematicRenderView { Key = "legacy", WidthPixels = 128, HeightPixels = 128,
            Region = new() { Position = new(), Size = new() { XNm = 100000000, YNm = 100000000 } } });
        var rendered = await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(views, token);
        Legacy(rendered.Snapshot.Data.Metadata);
        Assert.AreEqual(rendered.Snapshot.Revision, rendered.Views[0].Preview.Revision);
        Assert.IsTrue(rendered.Views[0].Preview.Png.Length > 24);

        Func<Task>[] unknown = [
            async () => await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document, SchemaVersion = 3 }, token),
            async () => await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document, SchemaVersion = 3 }, token),
            async () => await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document, SchemaVersion = 3 }, token),
            async () => await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document, SchemaVersion = 3 }, token),
            async () => { var request = views.Clone(); request.SchemaVersion = 3; await client.InvokeAsync<RenderSchematicViews, SchematicViewSet>(request, token); }
        ];
        foreach (var call in unknown)
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(call)).Status);
        Assert.AreEqual(current, await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, token));
    }
}
