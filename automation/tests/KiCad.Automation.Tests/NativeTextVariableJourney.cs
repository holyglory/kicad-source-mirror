using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyTextVariableBatch(NativeClient client, DocumentSpecifier root,
        int processId, string display, string rootFile, string evidence, CancellationToken token)
    {
        Task<SchematicHierarchyDataSnapshot> Read() =>
            client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
        var baseline = await Read(); var desired = baseline.Data.Clone();
        foreach (var screen in desired.Instances)
        {
            screen.Metadata.TextVariables["ENGINEERING_NOTE"] = "電源 & timing\nKeep close to CPU";
            screen.Metadata.TextVariables["EMPTY_NOTE"] = "";
        }
        var batch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = baseline.Revision,
            DocumentEpoch = baseline.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        batch.Operations.Add(SchematicHierarchyDelta.Plan(baseline.Data, desired));
        Assert.AreEqual(1, batch.Operations.Count);
        async Task Expanded(string expected)
        {
            var query = new ExpandTextVariables { Document = root }; query.Text.Add("${ENGINEERING_NOTE}");
            var result = await client.InvokeAsync<ExpandTextVariables, ExpandTextVariablesResponse>(query, token);
            Assert.AreEqual(expected, result.Text.Single());
        }
        string originalNote = baseline.Data.Instances[0].Metadata.TextVariables["ENGINEERING_NOTE"];
        await Expanded(originalNote);
        var rejected = batch.Clone(); rejected.OperationId = Guid.NewGuid().ToString("D");
        rejected.Operations.Add(new SchematicItemOperation());
        try
        {
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejected, token));
        }
        catch
        {
            NativeKeyboard.HasWindow(display, processId, "Schematic Editor", Console.WriteLine);
            var capture = new ProcessStartInfo("ffmpeg")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-nostdin", "-loglevel", "error", "-f", "x11grab", "-video_size", "1280x900",
                "-i", display, "-frames:v", "1", Path.Combine(evidence, "project-variables-failure.png") })
                capture.ArgumentList.Add(arg);
            using var process = Process.Start(capture)!;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(limit.Token); }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr);
            }
            throw;
        }
        Assert.AreEqual(baseline, await Read()); await Expanded(originalNote);
        var invalid = batch.Clone(); invalid.OperationId = Guid.NewGuid().ToString("D");
        invalid.Operations[0].ReplaceTextVariables.Variables.Add("", "No name");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalid, token));
        Assert.AreEqual(baseline, await Read());
        var applied = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.IsTrue(applied.TextVariablesChanged);
        var changed = await Read(); Assert.AreEqual(desired, changed.Data);
        await Expanded("電源 & timing\nKeep close to CPU");
        var reordered = batch.Clone(); reordered.Operations[0].ReplaceTextVariables.Variables.Clear();
        foreach (var entry in batch.Operations[0].ReplaceTextVariables.Variables.OrderByDescending(v => v.Key, StringComparer.Ordinal))
            reordered.Operations[0].ReplaceTextVariables.Variables.Add(entry.Key, entry.Value);
        for (int retry = 0; retry < 8; ++retry)
        {
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(retry % 2 == 0 ? batch : reordered, token);
            Assert.AreEqual(changed, await Read(), "Map serialization order must not change retry identity or repeat an edit.");
        }
        var changedRetry = batch.Clone(); changedRetry.Operations[0].ReplaceTextVariables.Variables["ENGINEERING_NOTE"] = "Different request";
        var retryError = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(changedRetry, token));
        StringAssert.Contains(retryError.Message, "different request"); Assert.AreEqual(changed, await Read());
        var noop = batch.Clone(); noop.ExpectedRevision = changed.Revision; noop.OperationId = Guid.NewGuid().ToString("D");
        var noChange = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(noop, token);
        Assert.IsFalse(noChange.TextVariablesChanged);
        Assert.AreEqual(changed.Revision, noChange.Revision, "A no-op result retains the observed revision.");
        Assert.IsFalse(noChange.TrackingComplete);
        Assert.AreEqual(changed, await Read());
        async Task Save(SchematicHierarchyData expected)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            var values = project.RootElement.GetProperty("text_variables");
            Assert.AreEqual(expected.Instances[0].Metadata.TextVariables.Count, values.EnumerateObject().Count());
            foreach (var (name, value) in expected.Instances[0].Metadata.TextVariables)
                Assert.AreEqual(value, values.GetProperty(name).GetString());
        }
        async Task History(string key, SchematicHierarchyData expected)
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicHierarchyDataSnapshot next;
            do
            {
                next = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, deadline.Token);
                if (next.Revision.Equals(before.Revision)) await Task.Delay(100, deadline.Token);
            } while (next.Revision.Equals(before.Revision));
            Assert.IsTrue(expected.Equals(next.Data), "Variable undo/redo must preserve all supported design state.");
        }
        await Save(changed.Data);
        await History("z", baseline.Data); await Expanded(originalNote);
        await History("y", changed.Data); await Expanded("電源 & timing\nKeep close to CPU");
        var noVariables = changed.Data.Clone();
        foreach (var screen in noVariables.Instances) screen.Metadata.TextVariables.Clear();
        var beforeRemove = await Read();
        var remove = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeRemove.Revision,
            DocumentEpoch = beforeRemove.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        remove.Operations.Add(SchematicHierarchyDelta.Plan(beforeRemove.Data, noVariables));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
        await Save(noVariables); await Expanded("${ENGINEERING_NOTE}");
        await History("z", changed.Data); await Expanded("電源 & timing\nKeep close to CPU");
        await History("z", baseline.Data); await Expanded(originalNote); await Save(baseline.Data);

        var mergeBaseline = await Read(); var nativeEdit = mergeBaseline.Data.Clone();
        foreach (var sheet in nativeEdit.Instances) sheet.Metadata.TextVariables["NATIVE_NOTE"] = "Keep near connector";
        async Task Apply(SchematicHierarchyDataSnapshot before, SchematicHierarchyData after)
        {
            var change = new ApplySchematicItemBatch { Document = root, ExpectedRevision = before.Revision,
                DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            change.Operations.Add(SchematicHierarchyDelta.Plan(before.Data, after));
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(change, token);
        }
        await Apply(mergeBaseline, nativeEdit); var nativeVersion = await Read();
        var baselineRoot = mergeBaseline.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var nativeRoot = nativeVersion.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var xmlVersion = baselineRoot.Clone(); xmlVersion.Metadata.TextVariables["XML_NOTE"] = "Keep near CPU";
        var merge = SchematicItemMerge.Plan(baselineRoot, xmlVersion, nativeRoot);
        Assert.IsTrue(merge.CanApply);
        var mergedHierarchy = nativeVersion.Data.Clone();
        foreach (var sheet in mergedHierarchy.Instances)
        {
            sheet.Metadata.TextVariables.Clear(); sheet.Metadata.TextVariables.Add(merge.Merged!.Metadata.TextVariables);
        }
        await Apply(nativeVersion, mergedHierarchy); var mergedNative = await Read();
        Assert.AreEqual(mergedHierarchy, mergedNative.Data);
        foreach (var sheet in mergedNative.Data.Instances)
        {
            Assert.AreEqual("Keep near CPU", sheet.Metadata.TextVariables["XML_NOTE"]);
            Assert.AreEqual("Keep near connector", sheet.Metadata.TextVariables["NATIVE_NOTE"]);
        }
        var conflictingXml = baselineRoot.Clone(); conflictingXml.Metadata.TextVariables["NATIVE_NOTE"] = "Conflicting location";
        var conflict = SchematicItemMerge.Plan(baselineRoot, conflictingXml, nativeRoot);
        Assert.IsFalse(conflict.CanApply); Assert.IsNull(conflict.Merged);
        Assert.AreEqual(mergedNative, await Read(), "Conflict planning must not mutate the native editor.");
        await History("z", nativeVersion.Data); await History("z", mergeBaseline.Data);
    }
}
