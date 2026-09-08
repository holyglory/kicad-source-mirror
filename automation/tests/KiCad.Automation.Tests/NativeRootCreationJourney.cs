using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<DocumentSpecifier> VerifyEmptyRootCreation(NativeClient client, string path,
        string otherProjectPath, string evidence, string instanceId, string declaredRootId, CancellationToken token,
        string? publishedMcpExecutable = null)
    {
        Assert.IsFalse(File.Exists(path));
        var wrong = await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.CreateRootSchematicAsync(otherProjectPath, token));
        Assert.AreEqual(3, wrong.Status);
        Task<OpenDocumentResponse> CreateThroughMcp() => CreateRootThroughMcp(client.Endpoint, instanceId, path, evidence, token, publishedMcpExecutable);
        var created = await CreateThroughMcp();
        if (Guid.Parse(declaredRootId) != Guid.Empty)
            Assert.AreEqual(declaredRootId, created.Document.SheetPath.Path[0].Value,
                "Creating a missing file must preserve its declared project-root identity.");
        else
            Assert.AreNotEqual(Guid.Empty, Guid.Parse(created.Document.SheetPath.Path[0].Value));
        Assert.AreEqual((DocumentType)1, created.Document.Type);
        SchematicObservation observation;
        while (true)
        {
            try
            {
                observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                    new() { Document = created.Document }, token);
                break;
            }
            catch (NativeApiException error) when (error.Status is 4 or 7)
            { await Task.Delay(100, token); }
        }
        Assert.IsEmpty(observation.Snapshot.Data.Items);
        Assert.IsTrue((await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = created.Document }, token)).UnsavedSchematicChanges);
        Assert.IsFalse(File.Exists(path), "Native initialization must not silently save a document.");
        Assert.AreEqual(created, await CreateThroughMcp());
        Assert.AreEqual(observation, await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
            new() { Document = created.Document }, token), "Retry must preserve the dirty created document.");
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-created-root.png"),
            observation.Preview.Png.ToByteArray(), token);
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = created.Document }, token);
        Assert.IsTrue(File.Exists(path));
        byte[] saved = await File.ReadAllBytesAsync(path, token);
        Assert.AreEqual(created, await CreateThroughMcp());
        CollectionAssert.AreEqual(saved, await File.ReadAllBytesAsync(path, token));
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = created.Document }, token);
        var reopened = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = created.Document }, token);
        var expected = observation.Snapshot.Data.Clone();
        Assert.AreEqual(0u, expected.Metadata.LoadedNativeFormatVersion,
            "A newly initialized document has no loaded-file provenance.");
        Assert.AreNotEqual(0u, expected.Metadata.WriterNativeFormatVersion);
        expected.Metadata.LoadedNativeFormatVersion = expected.Metadata.WriterNativeFormatVersion;
        Assert.IsTrue(expected.Equals(reopened.Data), NativeSnapshotDifference.Describe(expected, reopened.Data));
        return created.Document;
    }
}
