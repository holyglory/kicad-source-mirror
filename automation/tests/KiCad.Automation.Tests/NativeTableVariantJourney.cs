using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyTableVariantEdits(NativeClient client, DocumentSpecifier root,
        string rootFile, int processId, string display, string evidence, CancellationToken token)
    {
        const string table = "Symbol Fields Table";
        void Key(string key, string title = table, bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, title, control,
                focusCanvas: false, altKey: alt);
        async Task Window(string title, bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (NativeKeyboard.HasWindow(display, processId, title) != visible)
                    await Task.Delay(100, limit.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "table-variant-window-failed.png"), token);
                throw new InvalidOperationException($"Expected variant window '{title}' visible={visible}; see retained screenshot.");
            }
        }
        Task<SchematicHierarchyDataSnapshot> Read() =>
            client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, token);
        async Task Open(string title, int tabs)
        {
            // The full-project Tools menu has ten selectable entries between
            // Bulk Edit Symbol Fields and its last entry, Reload Plugins.
            Key("t", "Schematic Editor", alt: true); Key("End", "Schematic Editor");
            for (int i = 0; i < 10; i++) Key("Up", "Schematic Editor");
            Key("Return", "Schematic Editor"); await Window(table, true);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "table-variants-open.png"), token);
            var geometry = new List<string>();
            // The variant list is immediately above its bottom button row.
            // A 75px offset hits the section label when only one row is visible.
            NativeKeyboard.SchematicShortcut(display, processId, "click", table, controlKey: false,
                focusCanvas: true, clickFromLeft: 40, clickFromBottom: 45, describe: geometry.Add);
            Key("Home"); Key("Down");
            await File.WriteAllLinesAsync(Path.Combine(evidence, "table-variant-window-geometry.txt"), geometry, token);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "table-variant-selection.png"), token);
            for (int i = 0; i < tabs; i++) Key("Tab");
            Key("space"); await Window(title, true);
        }
        async Task CloseTable()
        {
            Key("Escape"); await Window(table, false);
        }
        async Task Accept(string title)
        {
            NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                focusCanvas: true, clickFromRight: 60, clickFromBottom: 26);
            await Window(title, false); await CloseTable();
        }
        async Task<SchematicHierarchyDataSnapshot> UndoRedo(string key, SchematicHierarchyDataSnapshot previous)
        {
            NativeKeyboard.SchematicShortcut(display, processId, key);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var current = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, limit.Token);
                if (!current.Revision.Equals(previous.Revision)) return current;
                await Task.Delay(100, limit.Token);
            }
        }

        foreach (var (title, tabs, destination, description) in new[]
        {
            ("Rename Design Variant", 2, "renamed", "Rename Design Variant"),
            ("Copy Design Variant", 3, "Assembly_copy", "Copy Design Variant"),
            ("Edit Description for", 4, "table description", "Edit Variant Description")
        })
        {
            var before = await Read();
            await Open(title, tabs); Key("Escape", title); await Window(title, false); await CloseTable();
            Assert.AreEqual(before.Revision, (await Read()).Revision, "Cancelling a table variant dialog must not invent an edit.");

            if (tabs != 3)
            {
                await Open(title, tabs); await Accept(title);
                Assert.AreEqual(before.Revision, (await Read()).Revision, "Accepting unchanged table variant input must not invent an edit.");
            }

            await Open(title, tabs);
            if (tabs == 4)
                NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                    focusCanvas: true, clickFromLeft: 40, clickFromTop: 50);
            Key("a", title, control: true); Key("BackSpace", title);
            foreach (char c in destination) Key(c == ' ' ? "space" : c.ToString(), title);
            await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, $"table-variant-{tabs}-edit.png"), token);
            await Accept(title);

            var changed = await Read();
            Assert.IsTrue(changed.Revision.Sequence > before.Revision.Sequence, description);
            foreach (var sheet in changed.Data.Instances)
            {
                var variants = sheet.Metadata.VariantDescriptions;
                if (tabs == 4) Assert.AreEqual(destination, variants["Assembly"]);
                else
                {
                    Assert.IsTrue(variants.ContainsKey(destination));
                    Assert.AreEqual(tabs == 3, variants.ContainsKey("Assembly"));
                    Assert.AreEqual("original", variants[destination]);
                }
            }
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
            Assert.AreEqual(1, journal.Changes.Count);
            Assert.AreEqual(description, journal.Changes.Single().Description);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using (var project = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token)))
            {
                var variants = project.RootElement.GetProperty("schematic").GetProperty("variants").EnumerateArray().ToArray();
                string name = tabs == 4 ? "Assembly" : destination;
                Assert.AreEqual(tabs == 4 ? destination : "original", variants.Single(v => v.GetProperty("name").GetString() == name)
                    .GetProperty("description").GetString());
                if (tabs != 4) Assert.AreEqual(tabs == 3, variants.Any(v => v.GetProperty("name").GetString() == "Assembly"));
            }
            var oldSymbol = before.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
                .First(i => i.Is(SchematicSymbolInstance.Descriptor)).Unpack<SchematicSymbolInstance>();
            oldSymbol.ValueField.Text.Text_ = "stale table edit";
            var stale = new ApplySchematicItemBatch { Document = root, ExpectedRevision = before.Revision,
                DocumentEpoch = before.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
            stale.Operations.Add(new SchematicItemOperation { Update = Any.Pack(oldSymbol) });
            var rejected = await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
            StringAssert.Contains(rejected.Message.ToLowerInvariant(), "revision");
            Assert.AreEqual(changed, await Read());

            var undone = await UndoRedo("z", changed);
            Assert.AreEqual(before.Data, undone.Data, "Undo must restore every affected sheet and project variant.");
            var redone = await UndoRedo("y", undone);
            Assert.AreEqual(changed.Data, redone.Data);
            undone = await UndoRedo("z", redone);
            Assert.AreEqual(before.Data, undone.Data);
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"table-variant-{tabs}-state.xml"),
                SchematicDataXml.Write(changed.Data), token);
        }
    }
}
