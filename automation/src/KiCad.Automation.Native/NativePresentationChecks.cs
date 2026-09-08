using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record PresentationRepairTarget(Guid ObjectId, Guid? OwnerId, string? FieldName);
public sealed record NativePresentationCheck(PresentationReport Report,
    IReadOnlyList<PresentationRepairTarget> RepairTargets, IReadOnlyList<string> Limitations);

public static class NativePresentationChecks
{
    public static async Task<NativePresentationCheck> CheckAsync(NativeClient client, DocumentSpecifier document,
        PresentationPolicy policy, CancellationToken cancellationToken = default)
    {
        var facts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
            new() { Document = document }, cancellationToken);
        if (!facts.Document.Equals(document) || facts.Document.SheetPath is null || facts.Document.SheetPath.Path.Count == 0)
            throw Invalid("Native presentation facts identify another or missing sheet.");
        var objects = new List<PresentationObject>();
        var references = new List<Guid>();
        var targets = new List<PresentationRepairTarget>();
        foreach (var fact in facts.Objects)
        {
            var kind = fact.Kind switch
            {
                SchematicPresentationObject.Types.Kind.Graphic => PresentationObjectKind.Graphic,
                SchematicPresentationObject.Types.Kind.Image => PresentationObjectKind.Image,
                SchematicPresentationObject.Types.Kind.Text => PresentationObjectKind.Text,
                SchematicPresentationObject.Types.Kind.ReferenceDesignator => PresentationObjectKind.ReferenceDesignator,
                _ => throw Invalid("Unsupported native presentation object kind.")
            };
            Guid id = Identity(fact.Id);
            objects.Add(new(id, kind, Bounds(fact.Bounds), fact.Visible,
                fact.HasTextHeightNm ? fact.TextHeightNm / 1_000_000m : null, fact.Text,
                TextBounds: fact.TextBounds is null ? null : Bounds(fact.TextBounds)));
            if (kind == PresentationObjectKind.ReferenceDesignator) references.Add(id);
            targets.Add(new(id, fact.OwnerId is null ? null : Identity(fact.OwnerId),
                string.IsNullOrEmpty(fact.FieldName) ? null : fact.FieldName));
        }
        var sheet = new PresentationSheet(facts.Document.SheetPath.Path.Select(Identity).ToArray(),
            Bounds(facts.PageBounds), objects, references,
            facts.Wires.Select(w => new PresentationWire(Identity(w.Id), w.SignalKey,
                new(w.Start.XNm, w.Start.YNm), new(w.End.XNm, w.End.YNm))).ToArray(),
            facts.Junctions.Select(p => new PresentationPoint(p.XNm, p.YNm)).ToArray());
        var revision = new KiCad.Automation.Model.DocumentRevision(facts.Revision.Epoch, facts.Revision.Sequence);
        // The native extraction deliberately reports missing capabilities.
        var snapshot = new PresentationSnapshot(Identity(facts.Document.SheetPath.Path[0]), revision,
            facts.CoverageComplete && facts.Limitations.Count == 0, [sheet]);
        return new(PresentationVerifier.Verify(snapshot, policy), targets, facts.Limitations.ToArray());
    }

    private static PresentationBounds Bounds(Box2? box)
    {
        if (box?.Position is null || box.Size is null || box.Size.XNm < 0 || box.Size.YNm < 0)
            throw Invalid("Native presentation bounds are missing or inverted.");
        return new(box.Position.XNm, box.Position.YNm,
            checked(box.Position.XNm + box.Size.XNm), checked(box.Position.YNm + box.Size.YNm));
    }
    private static Guid Identity(KIID? id) => Guid.TryParseExact(id?.Value, "D", out var value) && value != Guid.Empty
        ? value : throw Invalid("Native presentation identity is missing or invalid.");
    private static AutomationException Invalid(string message) => new("invalid_native_presentation", message);
}
