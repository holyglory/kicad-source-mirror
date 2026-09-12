using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using System.Diagnostics;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNetChainNameDialog(NativeClient client, DocumentSpecifier root,
        string rootFile, string pinId, int processId, string display, string evidence, CancellationToken token)
    {
        const string title = "Name Net Chain", oldName = "AUTOMATION_PATH", newName = "renamedpath";
        var header = new ItemHeader { Document = root };
        bool capturedMenuStack = false;
        void Key(string key, string window = "Schematic Editor", bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, focusCanvas: false, altKey: alt);
        async Task Window(string window, bool visible)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, window) != visible)
                    await Task.Delay(50, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-dialog-failed.png"), CancellationToken.None);
                throw;
            }
        }
        async Task<SchematicHierarchyDataSnapshot> Read(CancellationToken cancellation)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { return await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, limit.Token); }
                catch (NativeApiException error) when (error.Status == 7) { await Task.Delay(50, limit.Token); }
            }
        }
        async Task Same(IMessage expected, IMessage actual, string name)
        {
            bool same = expected.Equals(actual);
            if (!same)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, name + "-expected.json"), SchematicJson.Formatter.Format(expected), token);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + "-actual.json"), SchematicJson.Formatter.Format(actual), token);
            }
            Assert.IsTrue(same, "Net-chain state differs; see retained " + name + " snapshots.");
        }
        async Task OpenChainMenu()
        {
            await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
            var select = new AddToSelection { Header = header }; select.Items.Add(new KIID { Value = pinId });
            var selection = await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
            Assert.IsTrue(selection.Items.Any(i => i.Is(SchematicPin.Descriptor) && i.Unpack<SchematicPin>().Id.Value == pinId),
                "The real native context menu requires the exact placed pin selection.");
            // Open the actual canvas context menu without a left click that
            // would clear the explicit pin selection. The keyboard Menu key
            // is not handled by this GAL canvas on GTK.
            NativeKeyboard.SchematicShortcut(display, processId, "right-click", controlKey: false,
                focusCanvas: true, clickFromLeft: 640, clickFromTop: 450);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-context-menu.png"), token);
            // The rendered menu ends with Select All, Unselect All, Zoom and
            // Grid. Net Chain precedes those four entries; Name precedes Create.
            Key("End"); for (int i = 0; i < 4; i++) Key("Up"); Key("Right");
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-actions-menu.png"), token);
            var windows = new List<string>();
            NativeKeyboard.SchematicShortcut(display, processId, "", describe: windows.Add);
            await File.WriteAllLinesAsync(Path.Combine(evidence, "chain-menu-windows.txt"), windows, token);
            bool assertion = windows.Any(w => w.Contains("map=2", StringComparison.Ordinal)
                && (w.Contains("assert", StringComparison.OrdinalIgnoreCase) || w.Contains("Debug Alert", StringComparison.OrdinalIgnoreCase)));
            if (assertion && !capturedMenuStack)
            {
                // Read-only diagnosis of this fixture-owned native process.
                // Keep the stack cold; do not continue through a native assertion.
                var start = new ProcessStartInfo("gdb") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "--batch", "--nx", "--quiet", "-p", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-ex", "set pagination off", "-ex", "thread apply all bt 40", "-ex", "detach" }) start.ArgumentList.Add(arg);
                using var debugger = Process.Start(start)!;
                Task stdout = Capture(debugger.StandardOutput, Path.Combine(evidence, "chain-menu-stack.stdout.log"));
                Task stderr = Capture(debugger.StandardError, Path.Combine(evidence, "chain-menu-stack.stderr.log"));
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
                try { await debugger.WaitForExitAsync(limit.Token); }
                finally { if (!debugger.HasExited) { debugger.Kill(); await debugger.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); }
                capturedMenuStack = true;
            }
            if (assertion)
                throw new InvalidOperationException("Native assertion while opening the net-chain menu; see retained window/stack evidence.");
        }
        async Task Open()
        {
            await OpenChainMenu();
            Key("End"); Key("Up"); Key("Return");
            await Window(title, true);
        }
        void Text(string value)
        {
            Key("a", title, control: true); Key("BackSpace", title);
            foreach (char c in value) Key(c.ToString(), title);
        }
        async Task Saved(bool renamed)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            var classes = project.RootElement.GetProperty("net_settings").GetProperty("net_chain_classes");
            Assert.AreEqual("fastbus", classes.GetProperty(renamed ? newName : oldName).GetString());
            Assert.IsFalse(classes.TryGetProperty(renamed ? oldName : newName, out _));
            Assert.AreEqual("preserved", classes.GetProperty("UNAFFECTED_CHAIN").GetString());
            string contents = await File.ReadAllTextAsync(rootFile, token);
            StringAssert.Contains(contents, "(net_chain \"" + (renamed ? newName : oldName) + "\"");
        }
        async Task<SchematicHierarchyDataSnapshot> History(string key, SchematicHierarchyDataSnapshot prior)
        {
            Key(key, control: true);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var value = await Read(limit.Token);
                if (!value.Revision.Equals(prior.Revision)) return value;
                await Task.Delay(50, limit.Token);
            }
        }

        await Saved(false); var before = await Read(token);
        await Open(); Key("Escape", title); await Window(title, false); await Same(before, await Read(token), "chain-cancel");
        await Open(); Key("Return", title); await Window(title, false); await Same(before, await Read(token), "chain-noop");
        foreach (string invalid in new[] { "", "UNRESOLVED_PATH" })
        {
            await Open(); Text(invalid); Key("Return", title); await Window(title, false);
            if (invalid.Length != 0) { await Window("Error", true); Key("Return", "Error"); await Window("Error", false); }
            await Same(before, await Read(token), "chain-rejected"); await Saved(false);
        }
        await Open(); Text(newName);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-name-edited.png"), token);
        Key("Return", title); await Window(title, false);
        var changed = await Read(token); Assert.IsTrue(changed.Revision.Sequence > before.Revision.Sequence);
        foreach (var sheet in changed.Data.Instances)
        {
            Assert.IsFalse(sheet.Metadata.NetChains.Any(c => c.Name == oldName));
            Assert.IsTrue(sheet.Metadata.NetChains.Single(c => c.Name == newName).Committed);
        }
        var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
        Assert.AreEqual(1, journal.Changes.Count); Assert.AreEqual("Name Net Chain", journal.Changes.Single().Description);
        await Saved(true);
        var stale = new ApplySchematicItemBatch { Document = root, DocumentEpoch = before.Revision.Epoch,
            ExpectedRevision = before.Revision, OperationId = Guid.NewGuid().ToString("D") };
        var definitions = new SchematicNetChainState();
        foreach (var chain in before.Data.Instances[0].Metadata.NetChains)
        { var copy = chain.Clone(); copy.Committed = false; definitions.Definitions.Add(copy); }
        stale.Operations.Add(new SchematicItemOperation { ReplaceNetChains = definitions });
        var error = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        StringAssert.Contains(error.Message.ToLowerInvariant(), "revision");
        await Same(changed, await Read(token), "chain-stale");
        var undone = await History("z", changed); await Same(before.Data, undone.Data, "chain-undo"); await Saved(false);
        var redone = await History("y", undone); await Same(changed.Data, redone.Data, "chain-redo"); await Saved(true);
        undone = await History("z", redone); await Same(before.Data, undone.Data, "chain-restored"); await Saved(false);

        // Replace terminal B with the exact currently selected pin A. The
        // native menu explicitly offers B here; A is the disabled no-op entry.
        var terminalBefore = await Read(token);
        var electricalBefore = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = root }, token);
        await OpenChainMenu(); Key("End"); Key("Up"); Key("Up"); Key("Right");
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-terminal-menu.png"), token);
        Key("Escape"); Key("Escape"); Key("Escape");
        await Same(terminalBefore, await Read(token), "chain-terminal-cancel");
        await OpenChainMenu(); Key("End"); Key("Up"); Key("Up"); Key("Right"); Key("End"); Key("Return");
        var terminalAfter = await Read(token);
        Assert.IsTrue(terminalAfter.Revision.Sequence > terminalBefore.Revision.Sequence);
        foreach (var screen in terminalAfter.Data.Instances)
        {
            var chain = screen.Metadata.NetChains.Single(c => c.Name == oldName);
            Assert.AreEqual(chain.From.Reference, chain.To.Reference);
            Assert.AreEqual(chain.From.Pin, chain.To.Pin);
        }
        var electricalAfter = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = root }, token);
        string[] Partition(SchematicElectricalState state) => state.Nets.Select(n =>
            string.Join("|", n.Sheets.SelectMany(s => s.Items.Select(id => string.Join('/', s.Path.Path.Select(p => p.Value)) + ":" + id.Value)).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(Partition(electricalBefore), Partition(electricalAfter), "Changing a chain endpoint must not rewire the circuit.");
        journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            { Document = root, DocumentEpoch = terminalBefore.Revision.Epoch, AfterSequence = terminalBefore.Revision.Sequence }, token);
        Assert.AreEqual(1, journal.Changes.Count);
        Assert.AreEqual("Replace terminal pin", journal.Changes.Single().Description);
        await Saved(false);
        var terminalUndo = await History("z", terminalAfter); await Same(terminalBefore.Data, terminalUndo.Data, "chain-terminal-undo");
        var terminalRedo = await History("y", terminalUndo); await Same(terminalAfter.Data, terminalRedo.Data, "chain-terminal-redo");
        terminalUndo = await History("z", terminalRedo); await Same(terminalBefore.Data, terminalUndo.Data, "chain-terminal-restore");
        await Saved(false);
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);

        // Schematic Setup remains a real native dialog. Exercise the lazy
        // net-chain page without manufacturing edits on Cancel or unchanged OK.
        var setupBaseline = await Read(token);
        async Task StableSetupGeometry()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            (int X, int Y, int Width, int Height)? previous = null;
            var quiet = Stopwatch.StartNew();
            while (true)
            {
                (int X, int Y, int Width, int Height) current = default;
                NativeKeyboard.SchematicShortcut(display, processId, "", "Schematic Setup",
                    observeGeometry: value => current = value);
                if (previous != current) { previous = current; quiet.Restart(); }
                else if (quiet.Elapsed >= TimeSpan.FromMilliseconds(200)) return;
                await Task.Delay(50, limit.Token);
            }
        }
        foreach (bool accept in new[] { false, true, true })
        {
            Key("f", alt: true); Key("End");
            for (int i = 0; i < 4; i++) Key("Up");
            Key("Return"); await Window("Schematic Setup", true);
            await StableSetupGeometry();
            // Measured native tree row. Lazy page resolution can resize the
            // dialog; wait for stable geometry before targeting its buttons.
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromLeft: 80, clickFromTop: 264);
            await StableSetupGeometry();
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-setup-page.png"), token);
            NativeKeyboard.SchematicShortcut(display, processId, "click", "Schematic Setup", controlKey: false,
                focusCanvas: true, clickFromRight: accept ? 60 : 150, clickFromBottom: 25);
            await Window("Schematic Setup", false);
            await Same(setupBaseline, await Read(token), accept ? "chain-setup-noop" : "chain-setup-cancel");
            await Saved(false);
        }
    }
}
