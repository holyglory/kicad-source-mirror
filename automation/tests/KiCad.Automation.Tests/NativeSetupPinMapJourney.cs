using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySetupPinMap(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Task<SchematicScreenDataSnapshot> Snapshot() => client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
            new() { Document = document }, token);
        var baseline = await Snapshot();
        var initialSave = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        var changed = baseline.Data.Clone();
        var inputRule = changed.Metadata.ErcSettings.PinMap.Single(x => (int)x.First == 1 && (int)x.Second == 1);
        Assert.IsTrue((int)inputRule.Conflict is >= 1 and <= 3);
        inputRule.Conflict = (SchematicErcPinConflict)((int)inputRule.Conflict % 3 + 1);
        foreach (bool accept in new[] { false, true })
        {
            NativeKeyboard.SchematicShortcut(display, processId, "f", controlKey: false, altKey: true);
            NativeKeyboard.SchematicShortcut(display, processId, "End", controlKey: false, focusCanvas: false);
            for (int i = 0; i < 4; ++i)
                NativeKeyboard.SchematicShortcut(display, processId, "Up", controlKey: false, focusCanvas: false);
            NativeKeyboard.SchematicShortcut(display, processId, "Return", controlKey: false, focusCanvas: false);
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            int delay = 25;
            while (true)
            {
                ready.Token.ThrowIfCancellationRequested();
                if (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                {
                    // A mapped window can precede entry into the modal event
                    // loop. Use the same native readiness condition as the
                    // established formatting journey before sending input.
                    try { await client.InvokeAsync<GetPageSettings, PageSettings>(new() { Document = document }, ready.Token); }
                    catch (NativeApiException busy) when (busy.Status == 7) { break; }
                }
                await Task.Delay(delay, ready.Token); delay = Math.Min(delay * 2, 500);
            }

            // Expanded category rows redirect to their first child. Six Down
            // steps from Formatting select the native Pin Conflicts Map page.
            NativeKeyboard.SchematicShortcut(display, processId, "Home", "Schematic Setup", false,
                clickFromLeft: 80, clickFromTop: 40);
            for (int i = 0; i < 6; ++i)
                NativeKeyboard.SchematicShortcut(display, processId, "Down", "Schematic Setup", false, false);
            NativeKeyboard.SchematicShortcut(display, processId, "Tab", "Schematic Setup", false, false);
            NativeKeyboard.SchematicShortcut(display, processId, "space", "Schematic Setup", false, false);
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + $"-setup-pinmap-{accept}-edited.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "Home", "Schematic Setup", false,
                clickFromLeft: 80, clickFromTop: 40);
            await NativeKeyboard.CaptureAsync(display,
                Path.Combine(evidence, instanceId + $"-setup-pinmap-{accept}-page-switched.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", false,
                clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            delay = 25;
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, "Schematic Setup"))
                { await Task.Delay(delay, ready.Token); delay = Math.Min(delay * 2, 500); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                var windows = new List<string>();
                NativeKeyboard.HasWindow(display, processId, "Schematic Setup", describe: windows.Add);
                await File.WriteAllLinesAsync(Path.Combine(evidence, instanceId + "-setup-pinmap-windows.txt"), windows, token);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-setup-pinmap-close-failed.png"), token);
                throw;
            }
            var result = await Snapshot();
            Assert.AreEqual(accept ? changed : baseline.Data, result.Data,
                "The clicked matrix value must stay provisional across page switches and preserve all other fields.");
            Assert.AreEqual(baseline.Revision.Sequence + (accept ? 1UL : 0UL), result.Revision.Sequence);
            if (!accept)
                Assert.AreEqual(initialSave, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                    new() { Document = document }, token));
        }
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await WaitFor(baseline.Data);
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false, focusCanvas: false,
            clickFromLeft: 280, clickFromTop: 43);
        await WaitFor(changed);
        NativeKeyboard.SchematicShortcut(display, processId, "z");
        await WaitFor(baseline.Data);
        await client.InvokeAsync<SaveDocument, Google.Protobuf.WellKnownTypes.Empty>(new() { Document = document }, token);

        async Task WaitFor(SchematicScreenData expected)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (true)
            {
                var state = await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                    new() { Document = document }, wait.Token);
                if (state.Data.Equals(expected)) return;
                await Task.Delay(delay, wait.Token); delay = Math.Min(delay * 2, 500);
            }
        }
    }
}
