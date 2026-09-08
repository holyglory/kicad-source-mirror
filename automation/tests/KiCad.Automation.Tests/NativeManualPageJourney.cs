using Kiapi.Common.Commands;
using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyManualPageSettings(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, string textId,
        HierarchyFixture hierarchy, CancellationToken token)
    {
        var query = new GetPageSettings { Document = document };
        var original = await client.InvokeAsync<GetPageSettings, PageSettings>(query, token);
        var childOriginal = await ChildPages();
        var rootTitle = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        var childTitlesOriginal = await ChildTitles();
        var before = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
        var cursor = new ReadSchematicChangeJournal
        {
            Document = document, DocumentEpoch = before.DocumentEpoch, AfterSequence = before.Sequence
        };
        NativeKeyboard.SchematicShortcut(display, processId, "F12");
        await CancelDialog();
        await DialogClosed();
        Assert.AreEqual(original, await StablePage());
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);

        NativeKeyboard.SchematicShortcut(display, processId, "F12");
        // The first focusable control is the paper-size choice. Home selects
        // the native list's first entry (A5). Click the rendered OK button:
        // Return on this GTK choice opens its dropdown instead of accepting.
        await DialogKey("Home");
        // Paper's "Export to other sheets" checkbox, localized in the retained
        // 1280x900 fixture dialog image (separate from title-block exports).
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Page Settings", false, true, 891, 454);
        // Export the title separately, preserving the other child title fields.
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Page Settings", false, true, 181, 463);
        await CaptureDialog("before-accept");
        NativeKeyboard.SchematicShortcut(display, processId, "click", "Page Settings", false, true, 60, 25);
        await CaptureDialog("after-accept");
        await DialogClosed();
        var changed = await StablePage();
        Assert.AreEqual(PageSize.PsA5, changed.PageSize);
        foreach (var childPage in await ChildPages()) Assert.AreEqual(changed, childPage);
        var exportedTitles = await ChildTitles();
        for (int i = 0; i < exportedTitles.Length; ++i)
        {
            var expectedTitle = childTitlesOriginal[i].Clone(); expectedTitle.Title = rootTitle.Title;
            Assert.AreEqual(expectedTitle, exportedTitles[i]);
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(hierarchy.Directory, "shared-child.kicad_sch"), token),
            "(paper \"A5\"");
        var committed = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token);
        Assert.AreEqual(before.Sequence + 1, committed.Sequence);
        Assert.AreEqual("Edit Page Settings", committed.Changes.Single().Description);
        cursor.AfterSequence = committed.Sequence;
        var stale = new ApplySchematicItemBatch
        {
            Document = document, DocumentEpoch = before.DocumentEpoch, OperationId = Guid.NewGuid().ToString("D"),
            ExpectedRevision = new() { Epoch = before.DocumentEpoch, Sequence = before.Sequence }
        };
        stale.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = textId } });
        var staleError = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(staleError.Message, "Stale document revision");
        var intact = new GetItemsById { Header = new ItemHeader { Document = document } };
        intact.Items.Add(new KIID { Value = textId });
        Assert.HasCount(1, (await client.InvokeAsync<GetItemsById, GetItemsResponse>(intact, token)).Items);
        Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);
        var preview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-manual-page.png"), preview.Png.ToByteArray(), token);

        foreach (var (key, kind, expectedPage) in new[]
        {
            ("z", SchematicChange.Types.Kind.Undo, original),
            ("y", SchematicChange.Types.Kind.Redo, changed),
            ("z", SchematicChange.Types.Kind.Undo, original)
        })
        {
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var undoDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            undoDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            bool reportedBusy = false;
            while (true)
            {
                SchematicChangeJournal undone;
                try { undone = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, undoDeadline.Token); }
                catch (NativeApiException error) when (error.Status == 7)
                {
                    if (!reportedBusy) { Console.WriteLine("Undo/redo blocked: " + error.Message); reportedBusy = true; }
                    // GTK can briefly disable the parent while finishing the
                    // cancelled modal's teardown. Wait for a stable journal,
                    // preserving the same bounded deadline and expected event.
                    await Task.Delay(100, undoDeadline.Token);
                    continue;
                }
                if (undone.Sequence > cursor.AfterSequence)
                {
                    Assert.AreEqual(kind, undone.Changes.Single().Kind);
                    Assert.AreEqual(expectedPage, await client.InvokeAsync<GetPageSettings, PageSettings>(query, undoDeadline.Token));
                    var childPages = await ChildPages();
                    for (int i = 0; i < childPages.Length; ++i)
                        Assert.AreEqual(kind == SchematicChange.Types.Kind.Redo ? changed : childOriginal[i], childPages[i]);
                    var titles = await ChildTitles();
                    for (int i = 0; i < titles.Length; ++i)
                        Assert.AreEqual(kind == SchematicChange.Types.Kind.Redo ? exportedTitles[i] : childTitlesOriginal[i], titles[i]);
                    cursor.AfterSequence = undone.Sequence;
                    break;
                }
                await Task.Delay(100, undoDeadline.Token);
            }
            if (kind == SchematicChange.Types.Kind.Undo)
            {
                // Opening/cancelling the dialog must not consume the pending
                // Redo entry. The following loop iteration exercises that Redo.
                // Use the visible Page Settings toolbar button after sheet
                // navigation; do not assume the canvas retained key focus.
                NativeKeyboard.SchematicShortcut(display, processId, "click",
                    clickFromLeft: 94, clickFromTop: 42);
                await CancelDialog();
                await DialogClosed();
                Assert.AreEqual(expectedPage, await StablePage());
                Assert.IsEmpty((await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(cursor, token)).Changes);
                var cancelledView = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
                Assert.AreEqual(cursor.AfterSequence, cancelledView.Revision.Sequence);
                Assert.IsFalse(cancelledView.Png.IsEmpty);
                await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-cancelled-page-" + cursor.AfterSequence + ".png"),
                    cancelledView.Png.ToByteArray(), token);
            }
        }
        await client.InvokeAsync<SaveDocument, Empty>(new() { Document = document }, token);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(hierarchy.Directory, "shared-child.kicad_sch"), token))
            .Contains("(paper \"A5\"", StringComparison.Ordinal));

        async Task<TitleBlockInfo[]> ChildTitles()
        {
            var titles = new List<TitleBlockInfo>();
            try
            {
                foreach (string id in new[] { hierarchy.First, hierarchy.Second })
                {
                    var child = document.Clone(); child.SheetPath.Path.Add(new KIID { Value = id });
                    await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = child }, token);
                    titles.Add(await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = child }, token));
                }
                return titles.ToArray();
            }
            finally
            {
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token);
            }
        }

        async Task<PageSettings[]> ChildPages()
        {
            var pages = new List<PageSettings>();
            try
            {
                foreach (string id in new[] { hierarchy.First, hierarchy.Second })
                {
                    var child = document.Clone(); child.SheetPath.Path.Add(new KIID { Value = id });
                    await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = child }, token);
                    pages.Add(await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = child }, token));
                }
                return pages.ToArray();
            }
            finally
            {
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = document }, token);
            }
        }

        async Task CaptureDialog(string stage)
        {
        var capture = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-nostdin", "-loglevel", "error", "-f", "x11grab", "-video_size", "1280x900",
            "-i", display, "-frames:v", "1", Path.Combine(evidence, instanceId + "-manual-page-dialog-" + stage + ".png") })
            capture.ArgumentList.Add(argument);
        using (var process = Process.Start(capture)!)
        {
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
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        }

        async Task DialogKey(string key)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                try
                {
                    // Mapping precedes wx/GTK modal initialization. Wait until
                    // the editor has entered its modal loop and disabled the
                    // parent; this also synchronizes with the native UI thread.
                    NativeKeyboard.SchematicShortcut(display, processId, "", "Page Settings", false, false);
                    bool modalReady = false;
                    try
                    {
                        await client.InvokeAsync<GetPageSettings, PageSettings>(query, limit.Token);
                    }
                    catch (NativeApiException busy) when (busy.Status == 7) { modalReady = true; }
                    if (!modalReady) { await Task.Delay(100, limit.Token); continue; }
                    NativeKeyboard.SchematicShortcut(display, processId, key, "Page Settings", false, false);
                    return;
                }
                catch (InvalidOperationException error) when (error.Message.StartsWith("Expected one 'Page Settings'", StringComparison.Ordinal)) { }
                await Task.Delay(100, limit.Token);
            }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await CaptureDialog("readiness-timeout");
                throw;
            }
        }

        async Task CancelDialog()
        {
            await DialogKey(""); // Wait for the exact viewable dialog.
            var blocked = new ApplySchematicItemBatch { Document = document, Description = "Must reject during modal edit" };
            blocked.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = textId } });
            Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(blocked, token))).Status);
            // Click the real Cancel button. A synthetic Escape sent to the
            // top-level GTK window does not reliably reach its focused widget
            // on the fixture's window-manager-free X server.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Page Settings", false, true, 150, 25);
        }

        async Task DialogClosed()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, "Page Settings"))
                    await Task.Delay(100, limit.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                NativeKeyboard.HasWindow(display, processId, "Page Settings", Console.WriteLine);
                await CaptureDialog("close-timeout-" + Guid.NewGuid().ToString("N"));
                throw;
            }
        }

        async Task<PageSettings> StablePage()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { return await client.InvokeAsync<GetPageSettings, PageSettings>(query, limit.Token); }
                catch (NativeApiException error) when (error.Status == 7) { }
                await Task.Delay(100, limit.Token);
            }
        }
    }
}
