using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNetChainNameDialog(NativeClient client, DocumentSpecifier root,
        string rootFile, string pinId, int processId, string display, string evidence, CancellationToken token)
    {
        const string title = "Name Net Chain", oldName = "AUTOMATION_PATH", newName = "renamedpath";
        var header = new ItemHeader { Document = root };
        void Key(string key, string window = "Schematic Editor", bool control = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, focusCanvas: false);
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
        async Task Open()
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
            // Zoom and Grid are the two final standard submenus. Net Chain
            // precedes them; Name precedes Create in the single-pin submenu.
            Key("End"); Key("Up"); Key("Up"); Key("Right"); Key("End"); Key("Up"); Key("Return");
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
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
    }
}
