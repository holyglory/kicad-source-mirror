using System.Numerics;

namespace KiCad.Automation.Model;

public readonly record struct PresentationPoint(long XNm, long YNm);
public sealed record PresentationBounds(long LeftNm, long TopNm, long RightNm, long BottomNm)
{
    public bool Contains(PresentationBounds other) => LeftNm <= other.LeftNm && TopNm <= other.TopNm
        && RightNm >= other.RightNm && BottomNm >= other.BottomNm;
    internal void Validate()
    {
        if (LeftNm > RightNm || TopNm > BottomNm) throw PresentationVerifier.Invalid("Inverted rendered bounds.");
    }
}

public enum PresentationObjectKind { Graphic, Image, Text, ReferenceDesignator }
// FullBounds are measured before clipping, not the already-cropped visible box.
// Visible incorporates native field, unit, layer and rendering visibility.
public sealed record PresentationObject(Guid Id, PresentationObjectKind Kind, PresentationBounds FullBounds,
    bool Visible, decimal? TextHeightMm = null, string? Text = null, PresentationBounds? ClipBounds = null,
    PresentationBounds? TextBounds = null);
// SignalKey is scoped to this document revision; it is not an XML net identity.
public sealed record PresentationWire(Guid Id, string SignalKey, PresentationPoint Start, PresentationPoint End);
public sealed record PresentationSheet(IReadOnlyList<Guid> SheetPath, PresentationBounds PageBounds,
    IReadOnlyList<PresentationObject> Objects, IReadOnlyList<Guid> RequiredDesignators,
    IReadOnlyList<PresentationWire> Wires, IReadOnlyList<PresentationPoint> Junctions);
public sealed record PresentationSnapshot(Guid DocumentId, DocumentRevision Revision, bool CoverageComplete,
    IReadOnlyList<PresentationSheet> Sheets);
// Font limits are caller-selected presentation policy, not an electrical standard.
public sealed record PresentationPolicy(decimal MinimumTextHeightMm, decimal MaximumTextHeightMm,
    int MaximumCrossingsPerSignal = 2);
public enum PresentationSeverity { Warning, Error, Unavailable }
// One localized repair region; crossing findings may contain several regions
// rather than requiring a close-up of the whole signal's bounding box.
public sealed record PresentationLocation(PresentationBounds Bounds, IReadOnlyList<Guid> ObjectIds);
public sealed record PresentationFinding(string Rule, PresentationSeverity Severity, string SheetPath,
    IReadOnlyList<Guid> ObjectIds, PresentationBounds? Bounds, decimal? Measured, decimal? Limit, string Message,
    string? SignalKey = null, IReadOnlyList<PresentationLocation>? Locations = null);
public sealed record PresentationReport(Guid DocumentId, DocumentRevision Revision, bool CoverageComplete,
    IReadOnlyList<PresentationFinding> Findings)
{
    public bool Clear => CoverageComplete && Findings.Count == 0;
}

/// <summary>Deterministic checks over native rendering facts. This module does
/// not manufacture bounds from screenshots or claim the native extractor exists.</summary>
public static class PresentationVerifier
{
    public static PresentationReport Verify(PresentationSnapshot snapshot, PresentationPolicy policy)
    {
        if (snapshot.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Revision.Epoch))
            throw Invalid("An identified document revision is required.");
        if (policy.MinimumTextHeightMm <= 0 || policy.MaximumTextHeightMm < policy.MinimumTextHeightMm
            || policy.MaximumCrossingsPerSignal < 0) throw Invalid("Invalid presentation policy.");
        var findings = new List<PresentationFinding>();
        if (!snapshot.CoverageComplete)
            findings.Add(new("coverage_incomplete", PresentationSeverity.Unavailable, "", [], null, null, null,
                "Native rendering facts are incomplete; absence of findings cannot establish a clear diagram."));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in snapshot.Sheets)
        {
            if (sheet.SheetPath.Count == 0 || sheet.SheetPath.Any(id => id == Guid.Empty))
                throw Invalid("An exact sheet-instance path is required.");
            string path = string.Join('/', sheet.SheetPath.Select(id => id.ToString("D")));
            if (!paths.Add(path)) throw Invalid("Duplicate sheet instance.");
            sheet.PageBounds.Validate();
            var ids = new HashSet<Guid>();
            foreach (var item in sheet.Objects)
            {
                if (item.Id == Guid.Empty || !ids.Add(item.Id) || !Enum.IsDefined(item.Kind))
                    throw Invalid("Invalid or duplicate rendered object identity.");
                item.FullBounds.Validate(); item.ClipBounds?.Validate(); item.TextBounds?.Validate();
                if (item.Visible && !sheet.PageBounds.Contains(item.FullBounds))
                    Add("page_overflow", PresentationSeverity.Error, [item.Id], item.FullBounds, null, null,
                        "Rendered content extends beyond this sheet's page.");
                bool clipped = item.ClipBounds is not null && !item.ClipBounds.Contains(item.FullBounds);
                if (item.Visible && item.Kind == PresentationObjectKind.Image
                    && (clipped || !sheet.PageBounds.Contains(item.FullBounds)))
                    Add("image_cropped", PresentationSeverity.Error, [item.Id], item.FullBounds, null, null,
                        "Part of the image is outside its clip region or page.");
                if (item.Visible && item.Kind is PresentationObjectKind.Text or PresentationObjectKind.ReferenceDesignator)
                {
                    if (item.TextBounds is PresentationBounds textBounds && !string.IsNullOrWhiteSpace(item.Text))
                    {
                        if (!item.FullBounds.Contains(textBounds))
                            Add("text_container_overflow", PresentationSeverity.Warning, [item.Id], textBounds, null, null,
                                "Native text bounds extend beyond their container; enlarge the box or adjust the text layout.");
                        if (!sheet.PageBounds.Contains(textBounds) && sheet.PageBounds.Contains(item.FullBounds))
                            Add("page_overflow", PresentationSeverity.Error, [item.Id], textBounds, null, null,
                                "Text extends beyond this sheet's page even though its container fits.");
                    }
                    if (item.TextHeightMm is not decimal height || height <= 0)
                        Add("text_metrics_missing", PresentationSeverity.Unavailable, [item.Id], item.FullBounds, null, null,
                            "Effective native text height was not supplied.");
                    else if (height < policy.MinimumTextHeightMm || height > policy.MaximumTextHeightMm)
                        Add("text_size", PresentationSeverity.Warning, [item.Id], item.FullBounds, height,
                            height < policy.MinimumTextHeightMm ? policy.MinimumTextHeightMm : policy.MaximumTextHeightMm,
                            "Text height is outside the selected presentation policy.");
                }
            }
            var objects = sheet.Objects.ToDictionary(o => o.Id);
            if (sheet.RequiredDesignators.Distinct().Count() != sheet.RequiredDesignators.Count
                || sheet.RequiredDesignators.Contains(Guid.Empty)) throw Invalid("Invalid required designator identities.");
            foreach (var id in sheet.RequiredDesignators)
            {
                if (!objects.TryGetValue(id, out var field))
                    Add("designator_missing", PresentationSeverity.Error, [id], null, null, null,
                        "Required reference designator is absent from the rendering facts.");
                else if (field.Kind != PresentationObjectKind.ReferenceDesignator)
                    throw Invalid("Required designator identity resolves to another object kind.");
                else if (!field.Visible || string.IsNullOrWhiteSpace(field.Text)
                    || !sheet.PageBounds.Contains(field.FullBounds)
                    || (field.ClipBounds is not null && !field.ClipBounds.Contains(field.FullBounds)))
                    Add("designator_not_visible", PresentationSeverity.Error, [id], field.FullBounds, null, null,
                        "Required reference designator is hidden, empty or clipped.");
            }
            var wireIds = new HashSet<Guid>();
            foreach (var wire in sheet.Wires)
            {
                if (wire.Id == Guid.Empty || string.IsNullOrWhiteSpace(wire.SignalKey) || !wireIds.Add(wire.Id) || wire.Start == wire.End
                    || (objects.TryGetValue(wire.Id, out var graphical) && graphical.Kind != PresentationObjectKind.Graphic))
                    throw Invalid("Invalid wire or signal identity, duplicate object, or zero-length wire.");
                var bounds = WireBounds(wire);
                if (!objects.ContainsKey(wire.Id) && !sheet.PageBounds.Contains(bounds))
                    Add("page_overflow", PresentationSeverity.Error, [wire.Id], bounds, null, null,
                        "Wire geometry extends beyond this sheet's page.");
            }
            var crossings = new Dictionary<string, Dictionary<ExactIntersection, HashSet<Guid>>>(StringComparer.Ordinal);
            var ordered = sheet.Wires.OrderBy(w => Math.Min(w.Start.XNm, w.End.XNm)).ThenBy(w => w.Id).ToArray();
            for (int i = 0; i < ordered.Length; ++i)
            for (int j = i + 1; j < ordered.Length; ++j)
            {
                var first = ordered[i]; var second = ordered[j];
                if (Math.Min(second.Start.XNm, second.End.XNm) > Math.Max(first.Start.XNm, first.End.XNm)) break;
                if (first.SignalKey == second.SignalKey) continue;
                var crossing = Intersect(first, second);
                if (crossing is null)
                {
                    if (CollinearOverlap(first, second) is PresentationBounds overlap)
                        Add("overlapping_signals", PresentationSeverity.Warning, [first.Id, second.Id], overlap,
                            null, null, "Unrelated signals share a segment, making connectivity visually ambiguous.");
                    continue;
                }
                if (sheet.Junctions.Any(p => crossing == ExactIntersection.At(p)))
                {
                    Add("junction_net_conflict", PresentationSeverity.Error, [first.Id, second.Id], crossing.Bounds, null, null,
                        "A junction joins wires carrying different native signal identities; reconcile connectivity.");
                    continue;
                }
                Count(first.SignalKey, crossing, first.Id, second.Id);
                Count(second.SignalKey, crossing, first.Id, second.Id);
            }
            foreach (var (signal, locations) in crossings.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (locations.Count > policy.MaximumCrossingsPerSignal)
                    Add("excessive_crossings", PresentationSeverity.Warning,
                        locations.Values.SelectMany(v => v).Distinct().Order().ToArray(),
                        new(locations.Keys.Min(p => p.Bounds.LeftNm), locations.Keys.Min(p => p.Bounds.TopNm),
                            locations.Keys.Max(p => p.Bounds.RightNm), locations.Keys.Max(p => p.Bounds.BottomNm)),
                        locations.Count, policy.MaximumCrossingsPerSignal,
                        "Signal crosses unrelated wiring too many times on this sheet.", signal,
                        locations.OrderBy(p => p.Key.Bounds.LeftNm).ThenBy(p => p.Key.Bounds.TopNm)
                            .ThenBy(p => p.Key.Bounds.RightNm).ThenBy(p => p.Key.Bounds.BottomNm)
                            .Select(p => new PresentationLocation(p.Key.Bounds, p.Value.Order().ToArray())).ToArray());

            void Count(string signal, ExactIntersection location, Guid first, Guid second)
            {
                if (!crossings.TryGetValue(signal, out var locations)) crossings[signal] = locations = [];
                if (!locations.TryGetValue(location, out var involved)) locations[location] = involved = [];
                involved.Add(first); involved.Add(second);
            }
            void Add(string rule, PresentationSeverity severity, IReadOnlyList<Guid> objects,
                PresentationBounds? bounds, decimal? measured, decimal? limit, string message, string? signal = null,
                IReadOnlyList<PresentationLocation>? locations = null) =>
                findings.Add(new(rule, severity, path, objects, bounds, measured, limit, message, signal, locations));
        }
        if (snapshot.Sheets.Count == 0) throw Invalid("At least one sheet is required.");
        return new(snapshot.DocumentId, snapshot.Revision,
            snapshot.CoverageComplete && findings.All(f => f.Severity != PresentationSeverity.Unavailable), findings);
    }

    // Rational coordinates deduplicate exact intersections even when wires are
    // segmented differently. BigInteger avoids overflow at native coordinate limits.
    private sealed record ExactIntersection(BigInteger X, BigInteger Y, BigInteger Denominator)
    {
        public PresentationBounds Bounds => new(Round(X, false), Round(Y, false), Round(X, true), Round(Y, true));
        private long Round(BigInteger value, bool ceiling)
        {
            var quotient = BigInteger.DivRem(value, Denominator, out var remainder);
            return checked((long)(ceiling ? (remainder.Sign > 0 ? quotient + 1 : quotient)
                : (remainder.Sign < 0 ? quotient - 1 : quotient)));
        }
        public static ExactIntersection At(PresentationPoint p) => new(p.XNm, p.YNm, 1);
        public static ExactIntersection Create(BigInteger x, BigInteger y, BigInteger denominator)
        {
            if (denominator.Sign < 0) { x = -x; y = -y; denominator = -denominator; }
            var divisor = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(BigInteger.Abs(x), BigInteger.Abs(y)), denominator);
            return new(x / divisor, y / divisor, denominator / divisor);
        }
    }
    private static ExactIntersection? Intersect(PresentationWire a, PresentationWire b)
    {
        if (Math.Max(a.Start.XNm, a.End.XNm) < Math.Min(b.Start.XNm, b.End.XNm)
            || Math.Max(b.Start.XNm, b.End.XNm) < Math.Min(a.Start.XNm, a.End.XNm)
            || Math.Max(a.Start.YNm, a.End.YNm) < Math.Min(b.Start.YNm, b.End.YNm)
            || Math.Max(b.Start.YNm, b.End.YNm) < Math.Min(a.Start.YNm, a.End.YNm)) return null;
        BigInteger rx = (BigInteger)a.End.XNm - a.Start.XNm, ry = (BigInteger)a.End.YNm - a.Start.YNm;
        BigInteger sx = (BigInteger)b.End.XNm - b.Start.XNm, sy = (BigInteger)b.End.YNm - b.Start.YNm;
        BigInteger qx = (BigInteger)b.Start.XNm - a.Start.XNm, qy = (BigInteger)b.Start.YNm - a.Start.YNm;
        BigInteger denominator = rx * sy - ry * sx;
        if (denominator.IsZero) return null; // Collinear overlap is not an X/T crossing.
        BigInteger t = qx * sy - qy * sx, u = qx * ry - qy * rx;
        if (denominator.Sign < 0) { denominator = -denominator; t = -t; u = -u; }
        if (t < 0 || t > denominator || u < 0 || u > denominator) return null;
        return ExactIntersection.Create((BigInteger)a.Start.XNm * denominator + rx * t,
            (BigInteger)a.Start.YNm * denominator + ry * t, denominator);
    }
    private static PresentationBounds WireBounds(PresentationWire wire) => new(
        Math.Min(wire.Start.XNm, wire.End.XNm), Math.Min(wire.Start.YNm, wire.End.YNm),
        Math.Max(wire.Start.XNm, wire.End.XNm), Math.Max(wire.Start.YNm, wire.End.YNm));

    private static PresentationBounds? CollinearOverlap(PresentationWire a, PresentationWire b)
    {
        BigInteger rx = (BigInteger)a.End.XNm - a.Start.XNm, ry = (BigInteger)a.End.YNm - a.Start.YNm;
        bool OnLine(PresentationPoint point) => rx * ((BigInteger)point.YNm - a.Start.YNm)
            == ry * ((BigInteger)point.XNm - a.Start.XNm);
        if (!OnLine(b.Start) || !OnLine(b.End)) return null;
        var first = WireBounds(a); var second = WireBounds(b);
        var overlap = new PresentationBounds(Math.Max(first.LeftNm, second.LeftNm), Math.Max(first.TopNm, second.TopNm),
            Math.Min(first.RightNm, second.RightNm), Math.Min(first.BottomNm, second.BottomNm));
        return overlap.LeftNm <= overlap.RightNm && overlap.TopNm <= overlap.BottomNm
            && (overlap.LeftNm < overlap.RightNm || overlap.TopNm < overlap.BottomNm) ? overlap : null;
    }
    internal static AutomationException Invalid(string message) => new("invalid_presentation", message);
}
