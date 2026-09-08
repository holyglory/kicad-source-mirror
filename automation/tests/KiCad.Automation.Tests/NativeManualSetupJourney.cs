using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyManualSetup(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicChangeJournal> Journal() => client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
            new() { Document = document }, token);
        var before = await Journal();
        var baseline = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        var originalRatio = baseline.Data.Metadata.DrawingRatios.OverbarHeightRatio;
        var initialSaveState = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);
        Assert.AreNotEqual(1.2, originalRatio, "The edit fixture must actually change the native value.");
        foreach (var (accept, edit, stage) in new[]
                 { (false, false, "cancel"), (true, false, "unchanged"), (true, false, "unchanged-again"),
                   (false, true, "cancel-edit"), (true, true, "changed") })
        {
            // Use the real File menu. Its final entries are Setup, Page Settings,
            // Print, Plot and Close; menu keyboard navigation skips separators.
            NativeKeyboard.SchematicShortcut(display, processId, "f", controlKey: false, altKey: true);
            NativeKeyboard.SchematicShortcut(display, processId, "End", controlKey: false, focusCanvas: false);
            for (int i = 0; i < 4; ++i)
                NativeKeyboard.SchematicShortcut(display, processId, "Up", controlKey: false, focusCanvas: false);
            NativeKeyboard.SchematicShortcut(display, processId, "Return", controlKey: false, focusCanvas: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                {
                    try { await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = document }, timeout.Token); }
                    catch (NativeApiException busy) when (busy.Status == 7) { break; }
                }
                await Task.Delay(100, timeout.Token);
            }
            if (edit)
            {
                // Select Formatting from the native tree, then its second text
                // entry (Overbar offset ratio). No hidden editor command is used.
                NativeKeyboard.SchematicShortcut(display, processId, "Home", "Schematic Setup", false,
                    clickFromLeft: 80, clickFromTop: 40);
                // PAGED_DIALOG redirects the expanded General root to its
                // Formatting child. Moving Down again would leave that page.
                foreach (var key in new[] { "Tab", "Tab" })
                    NativeKeyboard.SchematicShortcut(display, processId, key, "Schematic Setup", false, false);
                NativeKeyboard.SchematicShortcut(display, processId, "a", "Schematic Setup", true, false);
                foreach (var key in new[] { "1", "2", "0" })
                    NativeKeyboard.SchematicShortcut(display, processId, key, "Schematic Setup", false, false);
            }
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + "-setup-" + stage + ".png"), token);
            if (accept)
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromRight: 60, clickFromBottom: 25);
            else
                // Escape can be consumed by the focused text-entry editor.
                // Exercise the visible dialog Cancel button instead.
                NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                    clickFromRight: 150, clickFromBottom: 25);
            while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                await Task.Delay(100, timeout.Token);
            var after = await Journal();
            Assert.AreEqual(before.DocumentEpoch, after.DocumentEpoch);
            Assert.AreEqual(before.Sequence + (accept && edit ? 1UL : 0UL), after.Sequence,
                edit && accept ? "Accepted settings change must advance the journal once."
                    : accept ? "Unchanged Schematic Setup must not manufacture an edit."
                    : "Cancelled setup must not record an edit.");
            if (edit && accept)
            {
                var changes = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(
                    new() { Document = document, DocumentEpoch = before.DocumentEpoch, AfterSequence = before.Sequence }, token);
                Assert.IsFalse(changes.ResetRequired);
                Assert.AreEqual("Edit Schematic Setup", changes.Changes.Single().Description);
            }
            var state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, token);
            Assert.AreEqual(accept && edit ? 1.2 : originalRatio, state.Data.Metadata.DrawingRatios.OverbarHeightRatio);
            var expected = baseline.Data.Clone();
            if (accept && edit) expected.Metadata.DrawingRatios.OverbarHeightRatio = 1.2;
            Assert.AreEqual(expected, state.Data, "Setup must preserve unrelated supported schematic state.");
            if (!(accept && edit))
                Assert.AreEqual(initialSaveState,
                    await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token),
                    "Cancelled or unchanged setup must preserve dirty-sheet state.");
        }
        var stale = new ApplySchematicItemBatch
        {
            Document = document, DocumentEpoch = baseline.Revision.Epoch, ExpectedRevision = baseline.Revision,
            OperationId = Guid.NewGuid().ToString("D")
        };
        stale.Operations.Add(new SchematicItemOperation { SetDrawingRatios = baseline.Data.Metadata.DrawingRatios.Clone() });
        var error = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(error.Message, "Stale document revision");
        Assert.AreEqual(1.2, (await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token)).Data.Metadata.DrawingRatios.OverbarHeightRatio,
            "Rejecting the stale request must preserve the user's accepted setting.");
        var changed = await Journal();
        stale.ExpectedRevision.Sequence = changed.Sequence;
        stale.OperationId = Guid.NewGuid().ToString("D");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token);
        Assert.AreEqual(originalRatio,
            (await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = document }, token)).Data.Metadata.DrawingRatios.OverbarHeightRatio);
        await client.InvokeAsync<SaveDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = document }, token);
    }
}
