using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMultiSheetBatch(NativeClient client, DocumentSpecifier root,
        int processId, string display, string rootFile, CancellationToken token)
    {
        Task<SchematicHierarchyDataSnapshot> Read(CancellationToken? local = null) =>
            client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = root }, local ?? token);
        var baseline = await Read();
        var rootData = baseline.Data.Instances.Single(s => s.Metadata.Document.Equals(root));
        var childData = baseline.Data.Instances.First(s => s.Metadata.Document.SheetPath.Path.Count == 2);
        var child = childData.Metadata.Document.Clone();
        var rootNote = rootData.Items.First(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        var childNote = childData.Items.First(i => i.Is(SchematicText.Descriptor)).Unpack<SchematicText>();
        var revisedRoot = rootNote.Clone(); revisedRoot.Text.Text_ += " parent edit";
        var revisedChild = childNote.Clone(); revisedChild.Text.Text_ += " child edit";
        var batch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = baseline.Revision,
            DocumentEpoch = baseline.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        var expected = baseline.Data.Clone();
        foreach (var screen in expected.Instances)
        {
            if (screen.Metadata.ScreenId.Equals(rootData.Metadata.ScreenId))
                screen.Items[screen.Items.IndexOf(Any.Pack(rootNote))] = Any.Pack(revisedRoot);
            if (screen.Metadata.ScreenId.Equals(childData.Metadata.ScreenId))
                screen.Items[screen.Items.IndexOf(Any.Pack(childNote))] = Any.Pack(revisedChild);
        }
        batch.Operations.Add(SchematicHierarchyDelta.Plan(baseline.Data, expected));
        foreach (bool wrongProject in new[] { false, true })
        {
            var bad = batch.Clone(); bad.OperationId = Guid.NewGuid().ToString("D");
            var wrong = child.Clone();
            if (wrongProject) wrong.Project.Name += "-other";
            else wrong.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            bad.Operations.Add(new SchematicItemOperation { TargetDocument = wrong, Update = Any.Pack(revisedChild) });
            await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(bad, token));
            Assert.AreEqual(baseline, await Read(), "A bad later target must roll back every earlier sheet edit.");
        }
        var applied = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        CollectionAssert.AreEqual(batch.Operations.Select(o => o.TargetDocument).ToArray(), applied.OperationTargets.ToArray());
        var changed = await Read();
        Assert.IsTrue(expected.Equals(changed.Data), "Both physical screens must change, including every repeated child instance.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(batch, token);
        Assert.AreEqual(changed, await Read(), "Cross-sheet retries must be idempotent.");
        var stale = batch.Clone(); stale.OperationId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(stale, token));
        Assert.AreEqual(changed, await Read());
        Assert.AreEqual(root, (await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = (DocumentType)1 }, token)).Documents.Single());
        await Shortcut("z", baseline.Data); await Shortcut("y", changed.Data); await Shortcut("z", baseline.Data);

        // Real concurrent versions: XML changes wording on two physical screens,
        // while the native editor has independently changed the root note's position.
        var beforeReview = await Read();
        var reviewNote = rootNote.Clone(); reviewNote.Text.Position.XNm += 1000000;
        var reviewEdit = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeReview.Revision,
            DocumentEpoch = beforeReview.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        reviewEdit.Operations.Add(new SchematicItemOperation { TargetDocument = root, Update = Any.Pack(reviewNote) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(reviewEdit, token);
        var reviewNative = await Read();
        var xmlVersion = (SchematicHierarchyData)SchematicDataXml.Read(SchematicDataXml.Write(expected));
        var reconciled = SchematicHierarchyMerge.Plan(baseline.Data, xmlVersion, reviewNative.Data, token);
        Assert.IsTrue(reconciled.CanApply, reconciled.ErrorMessage);
        Assert.AreEqual(2, reconciled.NativeOperations.Count, "Repeated sheets must not duplicate physical operations.");
        var mergeBatch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = reviewNative.Revision,
            DocumentEpoch = reviewNative.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        mergeBatch.Operations.Add(reconciled.NativeOperations);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(mergeBatch, token);
        var mergedSnapshot = await Read();
        Assert.AreEqual(reconciled.Merged, mergedSnapshot.Data);
        var mergedRoot = mergedSnapshot.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items
            .Where(i => i.Is(SchematicText.Descriptor)).Select(i => i.Unpack<SchematicText>()).Single(t => t.Id.Equals(rootNote.Id));
        Assert.AreEqual(revisedRoot.Text.Text_, mergedRoot.Text.Text_);
        Assert.AreEqual(reviewNote.Text.Position, mergedRoot.Text.Position);
        Assert.IsEmpty(SchematicHierarchyMerge.Plan(mergedSnapshot.Data, mergedSnapshot.Data, mergedSnapshot.Data, token).NativeOperations);
        await Shortcut("z", reviewNative.Data); await Shortcut("y", mergedSnapshot.Data);
        await Shortcut("z", reviewNative.Data); await Shortcut("z", baseline.Data);

        // The same UUID on different native screens must not alias in staged
        // creation or targeted deletion. Shared-sheet instances still alias.
        string sharedId = Guid.NewGuid().ToString("D");
        var newRoot = rootNote.Clone(); newRoot.Id.Value = sharedId; newRoot.Text.Text_ = "Root-only identity";
        var newChild = childNote.Clone(); newChild.Id.Value = sharedId; newChild.Text.Text_ = "Child-only identity";
        var beforeCreate = await Read();
        var create = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeCreate.Revision,
            DocumentEpoch = beforeCreate.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        create.Operations.Add(new SchematicItemOperation { TargetDocument = root, Create = Any.Pack(newRoot) });
        create.Operations.Add(new SchematicItemOperation { TargetDocument = child, Create = Any.Pack(newChild) });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(create, token);
        var created = await Read();
        Assert.IsTrue(created.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items.Contains(Any.Pack(newRoot)));
        Assert.IsTrue(created.Data.Instances.Where(s => s.Metadata.ScreenId.Equals(childData.Metadata.ScreenId)).All(s => s.Items.Contains(Any.Pack(newChild))));
        var remove = new ApplySchematicItemBatch { Document = root, ExpectedRevision = created.Revision,
            DocumentEpoch = created.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        remove.Operations.Add(new SchematicItemOperation { TargetDocument = child, Remove = newChild.Id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(remove, token);
        var removed = await Read();
        Assert.IsTrue(removed.Data.Instances.Single(s => s.Metadata.Document.Equals(root)).Items.Contains(Any.Pack(newRoot)),
            "Deleting a child object must not delete the root object with the same UUID.");
        Assert.IsTrue(removed.Data.Instances.Where(s => s.Metadata.ScreenId.Equals(childData.Metadata.ScreenId)).All(s => !s.Items.Contains(Any.Pack(newChild))));
        await Shortcut("z", created.Data); await Shortcut("z", baseline.Data);

        var newSheet = rootData.Items.First(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
        newSheet.Id.Value = Guid.NewGuid().ToString("D");
        newSheet.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        newSheet.FilenameField.Text.Text_ = "atomic-child-" + newSheet.Id.Value + ".kicad_sch";
        var newTarget = root.Clone(); newTarget.SheetPath.Path.Add(newSheet.Id.Clone());
        var contents = childNote.Clone(); contents.Id.Value = Guid.NewGuid().ToString("D");
        contents.Text.Text_ = "Created together with its parent sheet";
        var beforeSheet = await Read();
        var filledSheet = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeSheet.Revision,
            DocumentEpoch = beforeSheet.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        filledSheet.Operations.Add(new SchematicItemOperation { Create = Any.Pack(newSheet) });
        filledSheet.Operations.Add(new SchematicItemOperation { TargetDocument = newTarget, Create = Any.Pack(contents) });
        var nestedSheet = newSheet.Clone(); nestedSheet.Id.Value = Guid.NewGuid().ToString("D");
        nestedSheet.ChildScreenId.Value = Guid.NewGuid().ToString("D");
        nestedSheet.Path = newTarget.SheetPath.Clone(); nestedSheet.InstanceRecords = null;
        nestedSheet.FilenameField.Text.Text_ = "nested-" + nestedSheet.Id.Value + ".kicad_sch";
        var nestedTarget = newTarget.Clone(); nestedTarget.SheetPath.Path.Add(nestedSheet.Id.Clone());
        var nestedContents = contents.Clone(); nestedContents.Id.Value = Guid.NewGuid().ToString("D");
        nestedContents.Text.Text_ = "Nested contents in the same transaction";
        filledSheet.Operations.Add(new SchematicItemOperation { TargetDocument = newTarget, Create = Any.Pack(nestedSheet) });
        filledSheet.Operations.Add(new SchematicItemOperation { TargetDocument = nestedTarget, Create = Any.Pack(nestedContents) });
        filledSheet.Operations.Add(new SchematicItemOperation { TargetDocument = newTarget,
            SetTitleBlock = new() { Title = "New child title", Revision = "C1" } });
        filledSheet.Operations.Add(new SchematicItemOperation { TargetDocument = nestedTarget,
            SetTitleBlock = new() { Title = "Nested title", Revision = "N1" } });
        var desiredCreation = beforeSheet.Data.Clone();
        desiredCreation.Instances.Single(s => s.Metadata.Document.Equals(root)).Items.Add(Any.Pack(newSheet));
        SchematicScreenData NewScreen(DocumentSpecifier document, KIID identity, string title, string revision)
        {
            var metadata = rootData.Metadata.Clone();
            metadata.Document = document.Clone(); metadata.ScreenId = identity.Clone();
            metadata.LoadedNativeFormatVersion = 0; metadata.UnrepresentedState.Clear();
            metadata.RootInstance = null; metadata.TitleBlock = new() { Title = title, Revision = revision };
            return new() { Metadata = metadata };
        }
        var desiredChild = NewScreen(newTarget, newSheet.ChildScreenId, "New child title", "C1");
        desiredChild.Items.Add(Any.Pack(contents)); desiredChild.Items.Add(Any.Pack(nestedSheet));
        var desiredNested = NewScreen(nestedTarget, nestedSheet.ChildScreenId, "Nested title", "N1");
        desiredNested.Items.Add(Any.Pack(nestedContents));
        var nestedSymbol = rootData.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>())
            .OrderByDescending(s => s.DefinitionPinNameOffset?.ValueNm == 0)
            .ThenBy(s => s.Id.Value, StringComparer.Ordinal).First();
        Assert.IsTrue(nestedSymbol.SeparatePinIdentities);
        Assert.IsNotNull(nestedSymbol.DefinitionPinNameOffset);
        Assert.AreEqual(0L, nestedSymbol.DefinitionPinNameOffset.ValueNm,
            "The copied hierarchy symbol must exercise the zero owned-library offset regression.");
        // New physical screens own their cache definitions independently. A
        // placed symbol's inline data does not authorize inventing a missing
        // cache entry during an explicit full-cache transaction.
        var libraryLink = nestedSymbol.LibraryId ?? nestedSymbol.Definition.Id;
        string symbolCacheKey = nestedSymbol.LibName.Length != 0 ? nestedSymbol.LibName
            : (libraryLink.LibraryNickname.Length == 0 ? "" : libraryLink.LibraryNickname + ":")
                + libraryLink.EntryName;
        var cachedDefinition = rootData.CachedSymbols.Single(c => c.CacheKey == symbolCacheKey);
        desiredChild.CachedSymbols.Add(cachedDefinition.Clone());
        desiredNested.CachedSymbols.Add(cachedDefinition.Clone());
        nestedSymbol.Id.Value = Guid.NewGuid().ToString("D"); nestedSymbol.Path = nestedTarget.SheetPath.Clone();
        nestedSymbol.ReferenceField.Text.Text_ = "TP501"; nestedSymbol.Variants = new();
        foreach (var definitionItem in nestedSymbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = definitionItem.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id.Value = Guid.NewGuid().ToString("D");
            definitionItem.Item = Any.Pack(pin);
        }
        var nestedPlacement = new SymbolSheetRecord { ProjectName = root.Project.Name, Reference = "TP501",
            Unit = nestedSymbol.Unit.Unit, Variants = new() };
        nestedPlacement.Path.Add(nestedTarget.SheetPath.Path.Select(id => id.Clone()));
        nestedSymbol.InstanceRecords = new(); nestedSymbol.InstanceRecords.Records.Add(nestedPlacement);
        desiredNested.Items.Add(Any.Pack(nestedSymbol));
        var pairedSymbol = nestedSymbol.Clone(); pairedSymbol.Id.Value = Guid.NewGuid().ToString("D");
        pairedSymbol.ReferenceField.Text.Text_ = "TP502"; pairedSymbol.InstanceRecords.Records[0].Reference = "TP502";
        pairedSymbol.Position.XNm += 10000000;
        foreach (var definitionItem in pairedSymbol.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = definitionItem.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id.Value = Guid.NewGuid().ToString("D");
            definitionItem.Item = Any.Pack(pin);
        }
        desiredNested.Items.Add(Any.Pack(pairedSymbol));
        var connection = rootData.Items.First(i => i.Is(SchematicLine.Descriptor)).Unpack<SchematicLine>();
        connection.Id.Value = Guid.NewGuid().ToString("D"); connection.Start = nestedSymbol.Position.Clone(); connection.End = pairedSymbol.Position.Clone();
        desiredNested.Items.Add(Any.Pack(connection));
        var sheetPin = new SheetPin { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = nestedSheet.Position.Clone(), Text = childNote.Text.Clone(), Side = SheetSide.ShsLeft,
            Shape = SchematicLabelShape.SlshInput };
        sheetPin.Position.YNm += 5000000; sheetPin.Text.Text_ = "LINK";
        sheetPin.Text.Attributes.Multiline = false;
        nestedSheet.Pins.Add(sheetPin);
        int nestedSheetIndex = desiredChild.Items.ToList().FindIndex(i => i.Is(SheetSymbol.Descriptor));
        desiredChild.Items[nestedSheetIndex] = Any.Pack(nestedSheet);
        var portLabel = new HierarchicalLabel { Id = new() { Value = Guid.NewGuid().ToString("D") },
            Position = nestedSymbol.Position.Clone(), Text = childNote.Text.Clone(), Shape = SchematicLabelShape.SlshInput };
        portLabel.Text.Text_ = "LINK"; portLabel.Text.Attributes.Multiline = false; desiredNested.Items.Add(Any.Pack(portLabel));
        var parentProbe = nestedSymbol.Clone(); parentProbe.Id.Value = Guid.NewGuid().ToString("D");
        parentProbe.Path = newTarget.SheetPath.Clone(); parentProbe.Position = sheetPin.Position.Clone(); parentProbe.Position.XNm -= 10000000;
        parentProbe.ReferenceField.Text.Text_ = "TP503"; parentProbe.InstanceRecords.Records[0].Reference = "TP503";
        parentProbe.InstanceRecords.Records[0].Path.Clear(); parentProbe.InstanceRecords.Records[0].Path.Add(newTarget.SheetPath.Path.Select(id => id.Clone()));
        foreach (var definitionItem in parentProbe.Definition.Items.Where(i => i.Item.Is(SchematicPin.Descriptor)))
        {
            var pin = definitionItem.Item.Unpack<SchematicPin>(); pin.Id.Value = Guid.NewGuid().ToString("D");
            definitionItem.Item = Any.Pack(pin);
        }
        desiredChild.Items.Add(Any.Pack(parentProbe));
        var parentWire = connection.Clone(); parentWire.Id.Value = Guid.NewGuid().ToString("D");
        parentWire.Start = parentProbe.Position.Clone(); parentWire.End = sheetPin.Position.Clone(); desiredChild.Items.Add(Any.Pack(parentWire));
        string parentPin = parentProbe.Definition.Items.First(i => i.Item.Is(SchematicPin.Descriptor)).Item.Unpack<SchematicPin>().Id.Value;
        string firstPin = nestedSymbol.Definition.Items.First(i => i.Item.Is(SchematicPin.Descriptor)).Item.Unpack<SchematicPin>().Id.Value;
        string secondPin = pairedSymbol.Definition.Items.First(i => i.Item.Is(SchematicPin.Descriptor)).Item.Unpack<SchematicPin>().Id.Value;
        async Task VerifyConnection(DocumentSpecifier expectedTarget, bool parentConnected = true)
        {
            var netlist = await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = root }, token);
            var matching = netlist.Nets.Where(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == firstPin)).ToArray();
            Assert.AreEqual(1, matching.Length, "The first probe pin must belong to exactly one native net.");
            Assert.IsTrue(matching[0].Sheets.SelectMany(s => s.Items).Any(id => id.Value == secondPin), "Both probe pins must remain connected after hierarchy changes.");
            var targetSheets = matching[0].Sheets.Where(s => s.Items.Any(id => id.Value == firstPin || id.Value == secondPin)).ToArray();
            Assert.IsTrue(targetSheets.All(s => s.Path.Equals(expectedTarget.SheetPath)), "Connected pins must report their current sheet-instance path, not the pre-move path.");
            Assert.AreEqual(parentConnected, matching[0].Sheets.SelectMany(s => s.Items).Any(id => id.Value == parentPin),
                "Hierarchical label/sheet-pin binding must match the actual parent context.");
        }
        desiredCreation.Instances.Add(desiredChild); desiredCreation.Instances.Add(desiredNested);
        filledSheet.Operations.Clear();
        filledSheet.Operations.Add(SchematicHierarchyDelta.Plan(beforeSheet.Data, desiredCreation));
        var rejectedSheet = filledSheet.Clone(); rejectedSheet.OperationId = Guid.NewGuid().ToString("D");
        rejectedSheet.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejectedSheet, token));
        Assert.AreEqual(beforeSheet, await Read(), "Failed child population must roll back the newly staged sheet and contents.");
        var invalidLabelBatch = filledSheet.Clone(); invalidLabelBatch.OperationId = Guid.NewGuid().ToString("D");
        var invalidLabel = portLabel.Clone(); invalidLabel.Text.Attributes.Multiline = true;
        invalidLabelBatch.Operations.Add(new SchematicItemOperation { TargetDocument = nestedTarget, Update = Any.Pack(invalidLabel) });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalidLabelBatch, token));
        Assert.AreEqual(beforeSheet, await Read(), "Unsupported label state must reject without committing any staged sheet or contents.");
        var danglingChild = filledSheet.Clone(); danglingChild.OperationId = Guid.NewGuid().ToString("D");
        danglingChild.Operations.Add(new SchematicItemOperation { Remove = newSheet.Id });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(danglingChild, token));
        Assert.AreEqual(beforeSheet, await Read(), "Removing a populated staged parent must not leave orphan staged objects.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(filledSheet, token);
        var populated = await Read();
        var addedScreen = populated.Data.Instances.Single(s => s.Metadata.Document.Equals(newTarget));
        Assert.AreEqual(newSheet.ChildScreenId, addedScreen.Metadata.ScreenId);
        Assert.AreEqual("New child title", addedScreen.Metadata.TitleBlock.Title);
        Assert.IsTrue(addedScreen.Items.Contains(Any.Pack(contents)));
        var nestedScreen = populated.Data.Instances.Single(s => s.Metadata.Document.Equals(nestedTarget));
        Assert.AreEqual(nestedSheet.ChildScreenId, nestedScreen.Metadata.ScreenId);
        Assert.AreEqual("Nested title", nestedScreen.Metadata.TitleBlock.Title);
        Assert.IsTrue(nestedScreen.Items.Contains(Any.Pack(nestedContents)));
        await VerifyConnection(nestedTarget);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(filledSheet, token);
        Assert.AreEqual(populated, await Read());
        var mismatchedModel = populated.Data.Clone();
        var mismatchedScreen = mismatchedModel.Instances.Single(s => s.Metadata.Document.Equals(nestedTarget));
        int labelIndex = mismatchedScreen.Items.ToList().FindIndex(i => i.Is(HierarchicalLabel.Descriptor));
        var mismatchedLabel = mismatchedScreen.Items[labelIndex].Unpack<HierarchicalLabel>();
        mismatchedLabel.Text.Text_ = "OTHER_LINK"; mismatchedScreen.Items[labelIndex] = Any.Pack(mismatchedLabel);
        var mismatchBatch = new ApplySchematicItemBatch { Document = root, ExpectedRevision = populated.Revision,
            DocumentEpoch = populated.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        mismatchBatch.Operations.Add(SchematicHierarchyDelta.Plan(populated.Data, mismatchedModel));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(mismatchBatch, token);
        await VerifyConnection(nestedTarget, false);
        await Shortcut("z", populated.Data); await VerifyConnection(nestedTarget);
        populated = await Read();
        var desiredMove = populated.Data.Clone();
        var oldParent = desiredMove.Instances.Single(s => s.Metadata.Document.Equals(newTarget));
        var movedSheet = oldParent.Items.First(i => i.Is(SheetSymbol.Descriptor)).Unpack<SheetSymbol>();
        oldParent.Items.Remove(Any.Pack(movedSheet));
        movedSheet.Path = root.SheetPath.Clone();
        foreach (var placement in movedSheet.InstanceRecords.Records.Where(p => p.Path.SequenceEqual(newTarget.SheetPath.Path)))
        { placement.Path.Clear(); placement.Path.Add(root.SheetPath.Path.Select(id => id.Clone())); }
        desiredMove.Instances.Single(s => s.Metadata.Document.Equals(root)).Items.Add(Any.Pack(movedSheet));
        var movedTarget = root.Clone(); movedTarget.SheetPath.Path.Add(nestedSheet.Id.Clone());
        var movedData = desiredMove.Instances.Single(s => s.Metadata.Document.Equals(nestedTarget));
        movedData.Metadata.Document = movedTarget;
        for (int i = 0; i < movedData.Items.Count; ++i)
        {
            if (!movedData.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
            var placed = movedData.Items[i].Unpack<SchematicSymbolInstance>(); placed.Path = movedTarget.SheetPath.Clone();
            foreach (var placement in placed.InstanceRecords.Records.Where(p => p.Path.SequenceEqual(nestedTarget.SheetPath.Path)))
            { placement.Path.Clear(); placement.Path.Add(movedTarget.SheetPath.Path.Select(id => id.Clone())); }
            movedData.Items[i] = Any.Pack(placed);
        }
        foreach (var screen in desiredMove.Instances)
        {
            var ordered = SchematicItemDelta.Index(screen.Items).OrderBy(p => p.Key).Select(p => Any.Pack(p.Value)).ToArray();
            screen.Items.Clear(); screen.Items.Add(ordered);
        }
        var orderedMove = desiredMove.Instances.OrderBy(s => s.Metadata.Document.SheetPath.Path.Count)
            .ThenBy(s => string.Join("/", s.Metadata.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal).ToArray();
        desiredMove.Instances.Clear(); desiredMove.Instances.Add(orderedMove);
        var reparent = new ApplySchematicItemBatch { Document = root, ExpectedRevision = populated.Revision,
            DocumentEpoch = populated.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        var reparentMerge = SchematicHierarchyMerge.Plan(populated.Data, desiredMove, populated.Data, token);
        Assert.IsTrue(reparentMerge.CanApply, reparentMerge.ErrorMessage);
        reparent.Operations.Add(reparentMerge.NativeOperations);
        var rejectedMove = reparent.Clone(); rejectedMove.OperationId = Guid.NewGuid().ToString("D");
        rejectedMove.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejectedMove, token));
        Assert.AreEqual(populated, await Read());
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(reparent, token);
        var movedSnapshot = await Read();
        Assert.IsTrue(desiredMove.Equals(movedSnapshot.Data), "Reparenting must preserve screen/object identities, contents and explicit metadata.");
        await VerifyConnection(movedTarget, false);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(reparent, token);
        Assert.AreEqual(movedSnapshot, await Read());
        await Shortcut("z", populated.Data); await Shortcut("y", desiredMove); await Shortcut("z", populated.Data);
        await VerifyConnection(nestedTarget);
        populated = await Read();
        var removeSubtree = new ApplySchematicItemBatch { Document = root, ExpectedRevision = populated.Revision,
            DocumentEpoch = populated.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        removeSubtree.Operations.Add(SchematicHierarchyDelta.Plan(populated.Data, beforeSheet.Data));
        Assert.AreEqual(1, removeSubtree.Operations.Count);
        var rejectedRemoval = removeSubtree.Clone(); rejectedRemoval.OperationId = Guid.NewGuid().ToString("D");
        rejectedRemoval.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(rejectedRemoval, token));
        Assert.AreEqual(populated, await Read());
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(removeSubtree, token);
        var detached = await Read();
        Assert.IsTrue(beforeSheet.Data.Equals(detached.Data), "Removing a subtree must preserve every unrelated screen and object.");
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(removeSubtree, token);
        Assert.AreEqual(detached, await Read());
        await Shortcut("z", populated.Data); await Shortcut("y", beforeSheet.Data); await Shortcut("z", populated.Data);
        await Shortcut("z", beforeSheet.Data); await Shortcut("y", populated.Data); await Shortcut("z", beforeSheet.Data);

        var beforeCancel = await Read();
        var cancelEmpty = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeCancel.Revision,
            DocumentEpoch = beforeCancel.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        cancelEmpty.Operations.Add(new SchematicItemOperation { Create = Any.Pack(newSheet) });
        cancelEmpty.Operations.Add(new SchematicItemOperation { TargetDocument = newTarget,
            SetTitleBlock = new() { Title = "Cancelled child title" } });
        cancelEmpty.Operations.Add(new SchematicItemOperation { Remove = newSheet.Id });
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(cancelEmpty, token);
        Assert.IsTrue(beforeCancel.Data.Equals((await Read()).Data), "Cancelling a new empty sheet after metadata changes must preserve the loaded hierarchy.");

        var beforeAliases = await Read(); var aliasData = beforeAliases.Data.Clone();
        foreach (var screen in aliasData.Instances)
        {
            screen.Metadata.BusAliases.Clear();
            screen.Metadata.BusAliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "D0", "D1" } });
        }
        var aliases = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeAliases.Revision,
            DocumentEpoch = beforeAliases.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        aliases.Operations.Add(SchematicHierarchyDelta.Plan(beforeAliases.Data, aliasData));
        Assert.AreEqual(1, aliases.Operations.Count);
        var failedAliases = aliases.Clone(); failedAliases.OperationId = Guid.NewGuid().ToString("D");
        failedAliases.Operations.Add(new SchematicItemOperation());
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(failedAliases, token));
        Assert.AreEqual(beforeAliases, await Read(), "A rejected batch must restore project-wide alias definitions.");
        var invalidAliases = aliases.Clone(); invalidAliases.OperationId = Guid.NewGuid().ToString("D");
        invalidAliases.Operations[0].ReplaceBusAliases.Aliases[0].Members.Add(" ");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(invalidAliases, token));
        Assert.AreEqual(beforeAliases, await Read());
        var duplicateAliases = aliases.Clone(); duplicateAliases.OperationId = Guid.NewGuid().ToString("D");
        duplicateAliases.Operations[0].ReplaceBusAliases.Aliases.Add(new SchematicBusAlias { Name = "DATA", Members = { "OTHER" } });
        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(duplicateAliases, token));
        Assert.AreEqual(beforeAliases, await Read(), "Ambiguous names must reject before project persistence loses a definition.");
        var aliasResult = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(aliases, token);
        Assert.IsTrue(aliasResult.BusAliasesChanged);
        var aliased = await Read(); Assert.AreEqual(aliasData, aliased.Data);
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(aliases, token);
        Assert.AreEqual(aliased, await Read(), "Retry must not repeat the alias edit.");
        var unchangedAliases = aliases.Clone(); unchangedAliases.OperationId = Guid.NewGuid().ToString("D");
        unchangedAliases.ExpectedRevision = aliased.Revision;
        var unchangedResult = await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(unchangedAliases, token);
        Assert.IsFalse(unchangedResult.BusAliasesChanged); Assert.AreEqual(aliased, await Read());
        await Shortcut("z", beforeAliases.Data); await Shortcut("y", aliased.Data); await Shortcut("z", beforeAliases.Data);

        async Task SaveAndCheckAliases(SchematicHierarchyData expectedAliases)
        {
            await client.InvokeAsync<SaveDocument, Empty>(new() { Document = root }, token);
            using var project = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.ChangeExtension(rootFile, ".kicad_pro"), token));
            var persisted = project.RootElement.GetProperty("schematic").GetProperty("bus_aliases");
            var expectedDefinitions = expectedAliases.Instances[0].Metadata.BusAliases;
            Assert.AreEqual(expectedDefinitions.Count, persisted.EnumerateObject().Count());
            foreach (var alias in expectedDefinitions)
                CollectionAssert.AreEqual(alias.Members.ToArray(),
                    persisted.GetProperty(alias.Name).EnumerateArray().Select(v => v.GetString()!).ToArray());
        }
        var persistenceBaseline = await Read();
        var persistedAliases = aliases.Clone(); persistedAliases.OperationId = Guid.NewGuid().ToString("D");
        persistedAliases.ExpectedRevision = persistenceBaseline.Revision;
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(persistedAliases, token);
        await SaveAndCheckAliases(aliasData);
        var beforeRemoval = await Read(); var noAliases = beforeRemoval.Data.Clone();
        foreach (var screen in noAliases.Instances) screen.Metadata.BusAliases.Clear();
        var removeAliases = new ApplySchematicItemBatch { Document = root, ExpectedRevision = beforeRemoval.Revision,
            DocumentEpoch = beforeRemoval.Revision.Epoch, OperationId = Guid.NewGuid().ToString("D") };
        removeAliases.Operations.Add(SchematicHierarchyDelta.Plan(beforeRemoval.Data, noAliases));
        await client.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(removeAliases, token);
        await SaveAndCheckAliases(noAliases);
        await Shortcut("z", beforeRemoval.Data);
        await SaveAndCheckAliases(beforeRemoval.Data);
        await Shortcut("z", persistenceBaseline.Data);
        await SaveAndCheckAliases(persistenceBaseline.Data);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = root }, token);
        var reloadedAliases = await Read();
        foreach (var screen in reloadedAliases.Data.Instances)
            Assert.AreEqual(persistenceBaseline.Data.Instances[0].Metadata.BusAliases, screen.Metadata.BusAliases);

        async Task Shortcut(string key, SchematicHierarchyData expectedData)
        {
            var before = await Read(); NativeKeyboard.SchematicShortcut(display, processId, key);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicHierarchyDataSnapshot observed;
            do
            {
                observed = await Read(wait.Token);
                if (observed.Revision.Equals(before.Revision)) await Task.Delay(100, wait.Token);
            } while (observed.Revision.Equals(before.Revision));
            string expectedXml = SchematicDataXml.Write(expectedData), observedXml = SchematicDataXml.Write(observed.Data);
            int difference = Enumerable.Range(0, Math.Min(expectedXml.Length, observedXml.Length)).FirstOrDefault(i => expectedXml[i] != observedXml[i]);
            Assert.IsTrue(expectedData.Equals(observed.Data), "One undo/redo must restore the entire multi-sheet transaction. First difference expected "
                + expectedXml.Substring(difference, Math.Min(180, expectedXml.Length - difference))
                + " actual " + observedXml.Substring(difference, Math.Min(180, observedXml.Length - difference)));
        }
    }
}
