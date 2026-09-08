using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Protocol;
using KiCad.Automation.Native;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySharedRootOwnership(NativeClient client, DocumentSpecifier root,
        HierarchyFixture hierarchy, string rootFile, int processId, string display,
        string evidence, string instanceId, CancellationToken token)
    {
        var first = root.Clone(); first.SheetPath.Path.Add(new KIID { Value = hierarchy.First });
        var second = root.Clone(); second.SheetPath.Path.Add(new KIID { Value = hierarchy.Second });
        async Task Activate(DocumentSpecifier document) =>
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token);
        async Task<SchematicMetadataSnapshot> Read(DocumentSpecifier document)
        {
            await Activate(document);
            return await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new() { Document = document }, token);
        }
        async Task Set(DocumentSpecifier document, SchematicRootInstance value)
        {
            await Activate(document);
            var batch = new ApplySchematicItemBatch { Document = document, Description = "Shared file root page" };
            batch.Operations.Add(new SchematicItemOperation { SetRootInstance = value });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        }
        async Task CheckConflict(Func<Task> action)
        {
            var error = await Assert.ThrowsExactlyAsync<NativeApiException>(action);
            Assert.AreEqual(3, error.Status);
            StringAssert.Contains(error.Message, "Conflicting root page numbers");
        }
        async Task History(string key, SchematicChange.Types.Kind kind)
        {
            var before = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = root }, token);
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicChangeJournal changed;
            do
            {
                changed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = before.DocumentEpoch, AfterSequence = before.Sequence }, deadline.Token);
                if (changed.Changes.Count == 0) await Task.Delay(100, deadline.Token);
            } while (changed.Changes.Count == 0);
            Assert.AreEqual(kind, changed.Changes.Single().Kind);
        }
        try
        {
            var original = await Read(first);
            Assert.AreEqual(original.Metadata.RootInstance, (await Read(second)).Metadata.RootInstance);
            await Set(first, new() { PageNumber = "SHARED-ROOT" });
            Assert.AreEqual("SHARED-ROOT", (await Read(first)).Metadata.RootInstance.PageNumber);
            Assert.AreEqual("SHARED-ROOT", (await Read(second)).Metadata.RootInstance.PageNumber);
            await History("z", SchematicChange.Types.Kind.Undo);
            Assert.AreEqual(original.Metadata.RootInstance, (await Read(first)).Metadata.RootInstance);
            Assert.AreEqual(original.Metadata.RootInstance, (await Read(second)).Metadata.RootInstance);
            await History("y", SchematicChange.Types.Kind.Redo);
            Assert.AreEqual("SHARED-ROOT", (await Read(first)).Metadata.RootInstance.PageNumber);
            Assert.AreEqual("SHARED-ROOT", (await Read(second)).Metadata.RootInstance.PageNumber);

            // Deliberately create contradictory native copies, then verify
            // observation and persistence refuse to choose by traversal order.
            await Activate(root);
            var query = new GetItemsById { Header = new() { Document = root } };
            query.Items.Add(new KIID { Value = hierarchy.First });
            var sheet = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token)).Items.Single().Unpack<SheetSymbol>();
            // An unsaved visible change makes an early partial write detectable;
            // rewriting otherwise identical bytes would be a false-positive pass.
            sheet.Position.XNm += 1000000;
            sheet.InstanceRecords.Records.Single(r => r.Path.Count == 0).PageNumber = "CONFLICT";
            var conflict = new ApplySchematicItemBatch { Document = root, Description = "Isolated root conflict fixture" };
            conflict.Operations.Add(new SchematicItemOperation { Update = Any.Pack(sheet) });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(conflict, token);
            await CheckConflict(() => Read(first));
            await CheckConflict(() => Read(second));
            var files = new Dictionary<string, byte[]>();
            foreach (string path in Directory.EnumerateFiles(hierarchy.Directory)
                         .Where(p => Path.GetExtension(p) is ".kicad_sch" or ".kicad_pro"))
                files.Add(path, await File.ReadAllBytesAsync(path, token));
            await CheckConflict(() =>
                client.InvokeAsync<SaveDocument, Empty>(new() { Document = second }, token));
            Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<SaveCopyOfDocument, Empty>(new()
                {
                    Document = root, Path = rootFile, Options = new SaveOptions { Overwrite = true }
                }, token))).Status);
            // The ordinary graphical Save must fail before writing too.
            NativeKeyboard.SchematicShortcut(display, processId, "s");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                while (!NativeKeyboard.HasWindow(display, processId, "Error"))
                    await Task.Delay(100, deadline.Token);
                Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                    client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(
                        new() { Document = second, Description = "Modal mutation must be rejected" }, deadline.Token))).Status);
                var capture = new ProcessStartInfo("ffmpeg")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "-nostdin", "-loglevel", "error", "-f", "x11grab", "-video_size", "1280x900",
                             "-i", display, "-frames:v", "1", Path.Combine(evidence, instanceId + "-root-conflict.png") })
                    capture.ArgumentList.Add(argument);
                using var process = Process.Start(capture)!;
                try
                {
                    var output = process.StandardOutput.ReadToEndAsync(token);
                    var error = process.StandardError.ReadToEndAsync(token);
                    await process.WaitForExitAsync(token);
                    Assert.AreEqual(0, process.ExitCode, await error);
                    await output;
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                }
                NativeKeyboard.SchematicShortcut(display, processId, "Return", "Error", false, false);
                while (NativeKeyboard.HasWindow(display, processId, "Error"))
                    await Task.Delay(100, deadline.Token);
            }
            await CheckConflict(() => Read(second));
            foreach (var (path, bytes) in files)
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token), "A rejected save changed a schematic file.");

            await Set(second, new() { PageNumber = "RESOLVED" });
            Assert.AreEqual("RESOLVED", (await Read(first)).Metadata.RootInstance.PageNumber);
            Assert.AreEqual("RESOLVED", (await Read(second)).Metadata.RootInstance.PageNumber);
            await History("z", SchematicChange.Types.Kind.Undo);
            await CheckConflict(() => Read(first));
            await CheckConflict(() => Read(second));
            await History("y", SchematicChange.Types.Kind.Redo);
            Assert.AreEqual("RESOLVED", (await Read(first)).Metadata.RootInstance.PageNumber);
            Assert.AreEqual("RESOLVED", (await Read(second)).Metadata.RootInstance.PageNumber);
            NativeKeyboard.SchematicShortcut(display, processId, "s");
            byte[] savedRoot;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                do
                {
                    savedRoot = await File.ReadAllBytesAsync(rootFile, deadline.Token);
                    if (files[rootFile].SequenceEqual(savedRoot)) await Task.Delay(100, deadline.Token);
                } while (files[rootFile].SequenceEqual(savedRoot));
            }
            Assert.IsFalse(files[rootFile].SequenceEqual(savedRoot),
                "The resolved save must persist the previously unsaved layout change.");
            await client.InvokeAsync<SaveCopyOfDocument, Empty>(new()
            { Document = root, Path = rootFile, Options = new SaveOptions { Overwrite = true } }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = second }, token);
            // Reload may populate only one raw placement copy. File-level
            // metadata must still agree from either displayed instance.
            Assert.AreEqual("RESOLVED", (await Read(first)).Metadata.RootInstance.PageNumber);
            Assert.AreEqual("RESOLVED", (await Read(second)).Metadata.RootInstance.PageNumber);
            await Set(first, original.Metadata.RootInstance);
            Assert.AreEqual(original.Metadata.RootInstance, (await Read(second)).Metadata.RootInstance);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = second }, token);
        }
        finally { await Activate(root); }
    }
}
