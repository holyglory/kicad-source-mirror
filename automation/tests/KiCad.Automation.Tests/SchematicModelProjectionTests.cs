using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicModelProjectionTests
{
    [TestMethod]
    public void NativeMovesUpdatePresentationAndKeepConcurrentInstructionsAndSources()
    {
        var (baseline, library) = Fixture();
        var desired = baseline.Engineering with { Structure = baseline.Engineering.Structure with
            { Statements = baseline.Engineering.Structure.Statements.Select(s => s with { Text = s.Text + "\nPreserve this instruction." }).ToArray() } };
        var observed = baseline.Schematic.Clone();
        Edit(observed, s => { if (s.Unit.Unit == 1) s.Position.XNm += 1_000_000; });
        string before = SchematicDesignXml.Write(baseline, [library]);
        string after = SchematicDataXml.Write(observed);
        var result = SchematicModelProjection.Reconcile(baseline, desired, observed, [library]);
        Assert.IsNotNull(result.Candidate); Assert.IsEmpty(result.Conflicts);
        Assert.IsFalse(result.UnprojectedSnapshotChanges);
        Assert.AreEqual(2, result.CoverageGaps.Count);
        Assert.AreEqual(13.7m, result.Candidate.Circuit.Symbols[0].Placement!.XMillimeters);
        Assert.AreEqual(13.7m, result.Candidate.Circuit.Symbols[2].Placement!.XMillimeters);
        Assert.IsNull(result.Candidate.Circuit.Symbols[1].Placement);
        Assert.AreEqual(StructuralDiagramXml.Write(desired.Structure, desired.Circuit),
            StructuralDiagramXml.Write(result.Candidate.Structure, result.Candidate.Circuit));
        CollectionAssert.AreEqual(desired.ComponentBindings.ToArray(), result.Candidate.ComponentBindings.ToArray());
        Assert.AreEqual(before, SchematicDesignXml.Write(baseline, [library]));
        Assert.AreEqual(after, SchematicDataXml.Write(observed));
    }

    [TestMethod]
    public void ConcurrentPlacementEditsPreserveAllConflictValuesAndProduceNoCandidate()
    {
        var (baseline, library) = Fixture();
        var original = baseline.Engineering.Circuit.Symbols[0];
        var desired = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
            { Symbols = baseline.Engineering.Circuit.Symbols.Select(s => s.Id == original.Id
                ? s with { Placement = s.Placement! with { XMillimeters = 99m } } : s).ToArray() } };
        var observed = baseline.Schematic.Clone();
        Edit(observed, s => { if (s.Unit.Unit == 1) s.Position.XNm += 1_000_000; });
        var result = SchematicModelProjection.Reconcile(baseline, desired, observed, [library]);
        Assert.IsNull(result.Candidate);
        var conflict = result.Conflicts.Single();
        Assert.AreEqual(original.Id, conflict.ModelId); Assert.AreEqual("placement", conflict.Field);
        Assert.AreEqual(12.7m, conflict.Baseline.GetProperty("XMillimeters").GetDecimal());
        Assert.AreEqual(99m, conflict.Desired.GetProperty("XMillimeters").GetDecimal());
        Assert.AreEqual(13.7m, conflict.Native.GetProperty("XMillimeters").GetDecimal());
    }

    [TestMethod]
    public void SharedValuesAndMultiUnitReferencesAreReconciledAtTheirModelOwner()
    {
        var (baseline, library) = Fixture();
        var observed = baseline.Schematic.Clone();
        string firstPath = string.Join('/', observed.Instances[1].Metadata.Document.SheetPath.Path.Select(id => id.Value));
        Edit(observed, s =>
        {
            s.ValueField.Text.Text_ = "Updated value";
            if (string.Join('/', s.Path.Path.Select(id => id.Value)) == firstPath) s.ReferenceField.Text.Text_ = "U99";
        });
        var result = SchematicModelProjection.Reconcile(baseline, baseline.Engineering, observed, [library]);
        Assert.IsNotNull(result.Candidate);
        Assert.AreEqual("U99", result.Candidate.Circuit.Components[0].Reference);
        Assert.AreEqual("U2", result.Candidate.Circuit.Components[1].Reference);
        Assert.AreEqual("Updated value", result.Candidate.Circuit.Sheets.SelectMany(s => s.Components).Single().Value);
        var desired = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
            { Components = baseline.Engineering.Circuit.Components.Select((c, i) => i == 0 ? c with { Reference = "U77" } : c).ToArray() } };
        var conflict = SchematicModelProjection.Reconcile(baseline, desired, observed, [library]);
        Assert.IsNull(conflict.Candidate); Assert.AreEqual("reference", conflict.Conflicts.Single().Field);
    }

    [TestMethod]
    public void InconsistentUnitsSharedGeometryAndMissingFieldsDoNotGuess()
    {
        foreach (string problem in new[] { "reference", "unit", "missing-unit", "placement" })
        {
            var (baseline, library) = Fixture(); var observed = baseline.Schematic.Clone();
            var screen = observed.Instances[1]; int index = screen.Items.ToList().FindIndex(p => p.Is(SchematicSymbolInstance.Descriptor));
            var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>();
            switch (problem)
            {
                case "reference": symbol.ReferenceField.Text.Text_ = "U99"; break;
                case "unit": symbol.Unit.Unit = 2; break;
                case "missing-unit": symbol.Unit = null; break;
                case "placement": symbol.Position.XNm += 100; break;
            }
            screen.Items[index] = Any.Pack(symbol);
            var result = SchematicModelProjection.Reconcile(baseline, baseline.Engineering, observed, [library]);
            Assert.IsNull(result.Candidate, problem);
            Assert.IsTrue(result.Conflicts.Count > 0 || result.ErrorCode is not null, problem);
        }
    }

    [TestMethod]
    public void UnchangedCoordinateFreeModelDoesNotAcquireCoordinatesOrChurn()
    {
        var (baseline, library) = Fixture();
        baseline = baseline with { Engineering = baseline.Engineering with { Circuit = baseline.Engineering.Circuit.WithoutPlacement() } };
        var result = SchematicModelProjection.Reconcile(baseline, baseline.Engineering, baseline.Schematic.Clone(), [library]);
        Assert.IsNotNull(result.Candidate); Assert.IsFalse(result.UnprojectedSnapshotChanges);
        Assert.AreEqual(EngineeringDesignXml.Write(baseline.Engineering, [library]), EngineeringDesignXml.Write(result.Candidate, [library]));
    }

    [TestMethod]
    public void OtherNativeChangesRemainExplicitEvenWhenAPropertyCandidateExists()
    {
        var (baseline, library) = Fixture(); var observed = baseline.Schematic.Clone();
        observed.Instances[0].Items.Add(Any.Pack(new SchematicText { Id = new() { Value = Guid.NewGuid().ToString("D") }, Text = new() { Text_ = "Native addition" } }));
        var result = SchematicModelProjection.Reconcile(baseline, baseline.Engineering, observed, [library]);
        Assert.IsNotNull(result.Candidate);
        Assert.IsTrue(result.UnprojectedSnapshotChanges, "A property candidate must not pretend the whole native delta was reconciled.");
    }

    [TestMethod]
    public void MissingBindingsAndUnsupportedCoordinatePrecisionDoNotProducePartialUpdates()
    {
        var (baseline, library) = Fixture();
        var unbound = baseline with { SymbolBindings = [] };
        Assert.IsNull(SchematicModelProjection.Reconcile(unbound, baseline.Engineering, baseline.Schematic, [library]).Candidate);
        var observed = baseline.Schematic.Clone(); Edit(observed, s => s.Position.XNm += 1);
        var result = SchematicModelProjection.Reconcile(baseline, baseline.Engineering, observed, [library]);
        Assert.IsNull(result.Candidate); Assert.IsTrue(result.Conflicts.All(c => c.Reason == "invalid_precision"));
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicModelProjection.Reconcile(
            baseline, baseline.Engineering, observed, [library], new CancellationToken(canceled: true)));
        Assert.ThrowsExactly<OperationCanceledException>(() => new KnowledgeTools().ReconcileProperties("", "", "", [], new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public void RebindingAComponentDefinitionRequiresOwnershipReconciliation()
    {
        var (baseline, library) = Fixture();
        var replacement = baseline.Engineering.Circuit.Parts[0] with { Id = Guid.NewGuid(), Name = "Replacement part" };
        var desired = baseline.Engineering with { Circuit = baseline.Engineering.Circuit with
        {
            Parts = [.. baseline.Engineering.Circuit.Parts, replacement],
            Sheets = baseline.Engineering.Circuit.Sheets.Select(s => s with
                { Components = s.Components.Select(c => c with { PartId = replacement.Id }).ToArray() }).ToArray()
        } };
        desired.Validate([library]);
        var result = SchematicModelProjection.Reconcile(baseline, desired, baseline.Schematic, [library]);
        Assert.IsNull(result.Candidate);
        Assert.AreEqual("model_topology_changed", result.ErrorCode);
        Assert.IsTrue(result.UnprojectedSnapshotChanges);
    }

    [TestMethod]
    public void NativePinMapChangesCannotBeMistakenForCompletedPropertyReconciliation()
    {
        var (baseline, library) = Fixture(); var observed = baseline.Schematic.Clone();
        Edit(observed, s => s.PinMapOverride = new() { Mode = (PinMapOverrideMode)2, ActiveMapName = "routing-swap" });
        var result = SchematicModelProjection.Reconcile(baseline, baseline.Engineering, observed, [library]);
        Assert.IsNotNull(result.Candidate);
        Assert.IsTrue(result.UnprojectedSnapshotChanges);
        CollectionAssert.AreEqual(baseline.Engineering.Circuit.Nets.ToArray(), result.Candidate.Circuit.Nets.ToArray(),
            "Do not invent electrical pin changes from an unresolved native map selection.");
    }

    internal static (SchematicDesign, ComponentKnowledgeLibrary) Fixture()
    {
        var (design, library) = SchematicDesignTests.Fixture();
        Edit(design.Schematic, s =>
        {
            var placement = s.Unit.Unit == 1 ? design.Engineering.Circuit.Symbols[0].Placement! : new SymbolPlacement(30m, 25.4m, 0, false, false, false);
            s.Position = new() { XNm = Coordinates.MillimetersToNanometers(placement.XMillimeters), YNm = Coordinates.MillimetersToNanometers(placement.YMillimeters) };
            s.Transform = new() { Orientation = (SchematicSymbolOrientation)(placement.RotationDegrees / 90 + 1), MirrorX = placement.MirrorX, MirrorY = placement.MirrorY };
            s.Locked = (Kiapi.Common.Types.LockedState)(placement.Locked ? 2 : 1);
        });
        return (design, library);
    }

    private static void Edit(SchematicHierarchyData data, Action<SchematicSymbolInstance> change)
    {
        foreach (var screen in data.Instances)
        for (int i = 0; i < screen.Items.Count; i++)
            if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor))
            {
                var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>(); change(symbol); screen.Items[i] = Any.Pack(symbol);
            }
    }
}
