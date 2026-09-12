using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyInferredNetChainCreation(NativeClient client, DocumentSpecifier root,
        string rootFile, int processId, string display, string evidence, CancellationToken token)
    {
        // Reuse the real native connectivity fixture, with its embedded library.
        // The caller owns this disposable file and restores it in its finally.
        string native = await File.ReadAllTextAsync(Path.Combine(FindRoot(),
            "qa/data/eeschema/net_chains_four_nets.kicad_sch"), token);
        const string oldDeclaration = "\t(net_chain \"Signal1\"\n\t\t(uuid \"bff9f6c6-e9c4-49c9-8788-6c2b070b8b14\")\n\t\t(uuid \"bff9f6c6-e9c4-49c9-8788-6c2b070b8b15\")\n\t)\n";
        native = native.Replace("\r\n", "\n", StringComparison.Ordinal);
        StringAssert.Contains(native, oldDeclaration);
        native = native.Replace(oldDeclaration, "", StringComparison.Ordinal)
            .Replace("(path \"/899d2596-f6f5-4ac0-b36c-4a6df0ed51e2\"",
                "(path \"/" + root.SheetPath.Path[0].Value + "\"", StringComparison.Ordinal)
            .Replace("(project \"signals_four_nets\"", "(project \"" + Path.GetFileNameWithoutExtension(rootFile) + "\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(rootFile, native, token);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
        var header = new ItemHeader { Document = root };
        void Key(string key, string window = "Schematic Editor", bool control = false, bool alt = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, focusCanvas: false, altKey: alt);
        async Task Window(string title, bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (NativeKeyboard.HasWindow(display, processId, title) != visible)
                await Task.Delay(50, limit.Token);
        }
        async Task<SchematicHierarchyDataSnapshot> Read()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { return await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, limit.Token); }
                catch (NativeApiException error) when (error.Status == 7) { await Task.Delay(50, limit.Token); }
            }
        }
        async Task Same(IMessage expected, IMessage actual, string stage)
        {
            if (expected.Equals(actual)) return;
            await File.WriteAllTextAsync(Path.Combine(evidence, stage + "-expected.json"), SchematicJson.Formatter.Format(expected), token);
            await File.WriteAllTextAsync(Path.Combine(evidence, stage + "-actual.json"), SchematicJson.Formatter.Format(actual), token);
            Assert.Fail("Inferred-chain state differs; see retained " + stage + " snapshots.");
        }
        async Task<SchematicHierarchyDataSnapshot> History(string key, SchematicHierarchyDataSnapshot previous)
        {
            Key(key, control: true);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                var current = await Read();
                if (!current.Revision.Equals(previous.Revision)) return current;
                await Task.Delay(50, limit.Token);
            }
        }
        async Task<SchematicElectricalState> Electrical() =>
            await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = root }, token);
        string[] Partition(SchematicElectricalState state) => state.Nets.Select(n =>
            string.Join("|", n.Sheets.SelectMany(s => s.Items.Select(id => string.Join('/', s.Path.Path.Select(p => p.Value)) + ":" + id.Value)).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        var loaded = await Read();
        Assert.AreEqual(1, loaded.Data.Instances.Count);
        Assert.AreEqual(0, loaded.Data.Instances[0].Metadata.NetChains.Count);
        var symbols = loaded.Data.Instances[0].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "TP1", "TP2", "R1", "R2", "R3" }, symbols.Select(s => s.ReferenceField.Text.Text_).ToArray());
        string[] pins = symbols.Where(s => s.ReferenceField.Text.Text_.StartsWith("TP", StringComparison.Ordinal))
            .SelectMany(s => s.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor))
                .Select(i => i.Item.Unpack<SchematicPin>().Id.Value)).ToArray();
        Assert.AreEqual(2, pins.Length);
        var electrical = await Electrical();
        Assert.AreEqual(4, electrical.Nets.Count, "The inferred path must cross three resistors and four distinct electrical nets.");
        const string title = "Create Net Chain";
        foreach (bool shortcut in new[] { false, true })
        {
            string name = shortcut ? "betweenpins" : "inferredpath";
            async Task Open()
            {
                await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);
                if (shortcut)
                {
                    var select = new AddToSelection { Header = header };
                    select.Items.Add(pins.Select(p => new KIID { Value = p }));
                    var selected = await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
                    CollectionAssert.AreEquivalent(pins, selected.Items.Where(i => i.Is(SchematicPin.Descriptor))
                        .Select(i => i.Unpack<SchematicPin>().Id.Value).ToArray());
                    NativeKeyboard.SchematicShortcut(display, processId, "right-click", controlKey: false,
                        focusCanvas: true, clickFromLeft: 640, clickFromTop: 450);
                    Key("End"); for (int i = 0; i < 4; i++) Key("Up"); Key("Right");
                    await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, "chain-two-pin-menu.png"), token);
                    Key("End"); Key("Return");
                }
                else { Key("t", alt: true); Key("End"); Key("Up"); Key("Up"); Key("Return"); }
                await Window(title, true);
                await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, name + "-dialog.png"), token);
            }
            void Name()
            {
                if (!shortcut)
                    NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                        focusCanvas: true, clickFromLeft: 150, clickFromBottom: 70);
                Key("a", title, control: true);
                foreach (char c in name) Key(c.ToString(), title);
            }
            async Task Close(bool accept)
            {
                if (shortcut) Key(accept ? "Return" : "Escape", title);
                else
                {
                    if (accept)
                        NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                            focusCanvas: true, clickFromRight: 60, clickFromBottom: 25);
                    NativeKeyboard.SchematicShortcut(display, processId, "click", title, controlKey: false,
                        focusCanvas: true, clickFromRight: 150, clickFromBottom: 25);
                }
                await Window(title, false);
            }
            var before = await Read();
            await Open(); Name(); await Close(false);
            await Same(before, await Read(), name + "-cancel");
            await Open(); Name(); await Close(true);
            var created = await Read();
            var chain = created.Data.Instances.Single().Metadata.NetChains.Single(c => c.Name == name);
            Assert.IsTrue(chain.Committed);
            Assert.AreEqual(4, chain.MemberNets.Count);
            CollectionAssert.AreEquivalent(new[] { "TP1", "TP2" }, new[] { chain.From.Reference, chain.To.Reference });
            Assert.AreEqual("1", chain.From.Pin); Assert.AreEqual("1", chain.To.Pin);
            CollectionAssert.AreEqual(Partition(electrical), Partition(await Electrical()), "A net chain must not merge or rewire its member nets.");
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
                { Document = root, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence }, token);
            Assert.AreEqual(1, journal.Changes.Count);
            Assert.AreEqual("Create Net Chain", journal.Changes.Single().Description);
            var stale = new ApplySchematicItemBatch { Document = root, DocumentEpoch = before.Revision.Epoch,
                ExpectedRevision = before.Revision, OperationId = Guid.NewGuid().ToString("D") };
            stale.Operations.Add(new SchematicItemOperation { ReplaceNetChains = new() });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
            await Same(created, await Read(), name + "-stale");
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            StringAssert.Contains(await File.ReadAllTextAsync(rootFile, token), "(net_chain \"" + name + "\"");
            var undone = await History("z", created); await Same(before.Data, undone.Data, name + "-undo");
            var redone = await History("y", undone); await Same(created.Data, redone.Data, name + "-redo");
            // Save/reload verifies the committed declaration rebuilds the real
            // chain; then remove it through the existing revision-safe delta.
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
            var reopened = await Read(); await Same(created.Data, reopened.Data, name + "-reload");
            var remove = new ApplySchematicItemBatch { Document = root, DocumentEpoch = reopened.Revision.Epoch,
                ExpectedRevision = reopened.Revision, OperationId = Guid.NewGuid().ToString("D") };
            remove.Operations.Add(new SchematicItemOperation { ReplaceNetChains = new() });
            await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
            await Same(before.Data, (await Read()).Data, name + "-restored");
        }
    }
}
