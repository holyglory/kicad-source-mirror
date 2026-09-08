using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;

namespace KiCad.Automation.Native;

public sealed record HierarchyTopologyIssue(string Code, string InstancePath, string? ObjectId, string Message);
public sealed record HierarchyCoverageGap(string InstancePath, string Reason);
public sealed record HierarchyTopologyReport(bool IsValid, int InstanceCount, int ScreenCount,
    IReadOnlyList<HierarchyTopologyIssue> Issues, IReadOnlyList<HierarchyCoverageGap> CoverageGaps);

/// <summary>Checks captured instance/reference topology, not electrical correctness,
/// filesystem resolution, shared object equivalence or reconstruction readiness.</summary>
public static class SchematicHierarchyTopology
{
    public static HierarchyTopologyReport Inspect(SchematicHierarchyData hierarchy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Do not silently discard transport fields which this model cannot represent.
        SchematicDataXml.Write(hierarchy);
        var issues = new List<HierarchyTopologyIssue>();
        var gaps = new List<HierarchyCoverageGap>();
        var instances = new SortedDictionary<string, SchematicScreenData>(StringComparer.Ordinal);
        var sharedEdges = new Dictionary<string, string>(StringComparer.Ordinal);
        void Issue(string code, string path, string? id, string message) => issues.Add(new(code, path, id, message));
        string? PathKey(SheetPath? path) => path is not null && path.Path.Count > 0 && path.Path.All(ValidId)
            ? string.Join("/", path.Path.Select(i => i.Value)) : null;
        string? root = PathKey(hierarchy.Document?.SheetPath);
        if (root is null || hierarchy.Document!.SheetPath.Path.Count != 1)
            Issue("invalid_root", "", null, "A hierarchy requires one canonical root sheet-instance identity.");

        foreach (var screen in hierarchy.Instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = screen.Metadata;
            string? path = PathKey(metadata?.Document?.SheetPath);
            if (path is null || !ValidId(metadata?.ScreenId))
            {
                Issue("invalid_instance", path ?? "", metadata?.ScreenId?.Value, "Every instance requires canonical path and screen identities.");
                continue;
            }
            if (!instances.TryAdd(path, screen))
                Issue("duplicate_instance", path, null, "The same sheet-instance path occurs more than once.");
            var owner = metadata!.Document.Clone(); owner.SheetPath = hierarchy.Document?.SheetPath?.Clone();
            if (!owner.Equals(hierarchy.Document))
                Issue("different_document", path, null, "All instances must belong to the declared schematic document and project.");
            foreach (string missing in metadata.UnrepresentedState) gaps.Add(new(path, missing));
            foreach (var missing in screen.UnrepresentedItems)
                gaps.Add(new(path, $"Object {missing.Id?.Value}: {missing.Reason}"));
        }

        if (root is not null && !instances.ContainsKey(root))
            Issue("missing_root", root, null, "The declared root instance must be present.");

        foreach (var (path, screen) in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root is null || (path != root && !path.StartsWith(root + "/", StringComparison.Ordinal)))
                Issue("outside_root", path, null, "The instance is outside the declared root hierarchy.");
            if (path != root)
            {
                int separator = path.LastIndexOf('/');
                string parentPath = separator < 0 ? "" : path[..separator];
                string symbolId = separator < 0 ? path : path[(separator + 1)..];
                if (!instances.TryGetValue(parentPath, out var parent))
                    Issue("missing_parent", path, symbolId, "The parent instance is absent.");
                else
                {
                    var links = Sheets(parent).Where(s => s.Id?.Value == symbolId).ToArray();
                    if (links.Length != 1 || links[0].ChildScreenId?.Value != screen.Metadata.ScreenId.Value)
                        Issue("orphan_instance", path, symbolId, "Exactly one parent sheet symbol must reference this child screen.");
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var edges = new List<string>();
            foreach (var sheet in Sheets(screen))
            {
                if (!ValidId(sheet.Id) || !ValidId(sheet.ChildScreenId) || !seen.Add(sheet.Id.Value))
                {
                    Issue("invalid_sheet_reference", path, sheet.Id?.Value, "Sheet references need unique object IDs and canonical child screen IDs.");
                    continue;
                }
                if (!Equals(sheet.Path, screen.Metadata.Document.SheetPath))
                    Issue("wrong_parent_path", path, sheet.Id.Value, "The sheet symbol reports a different parent instance.");
                if (string.IsNullOrWhiteSpace(sheet.FilenameField?.Text?.Text_) || sheet.FilenameField.Text.Text_.Contains('\0'))
                    Issue("missing_child_filename", path, sheet.Id.Value, "The referenced child file must be explicitly named.");
                string childPath = path + "/" + sheet.Id.Value;
                if (!instances.TryGetValue(childPath, out var child))
                    Issue("missing_child", path, sheet.Id.Value, "The referenced child's contents are absent from the hierarchy.");
                else if (child.Metadata.ScreenId.Value != sheet.ChildScreenId.Value)
                    Issue("wrong_child_screen", childPath, sheet.Id.Value, "The captured child screen identity differs from its parent reference.");
                edges.Add(sheet.Id.Value + ":" + sheet.ChildScreenId.Value);
            }
            string topology = string.Join(";", edges.Order(StringComparer.Ordinal));
            if (sharedEdges.TryGetValue(screen.Metadata.ScreenId.Value, out var previous) && previous != topology)
                Issue("conflicting_shared_topology", path, screen.Metadata.ScreenId.Value, "Instances of one native screen disagree about child references.");
            else sharedEdges.TryAdd(screen.Metadata.ScreenId.Value, topology);

            string ancestor = path;
            while (true)
            {
                int split = ancestor.LastIndexOf('/');
                if (split < 0) break;
                ancestor = ancestor[..split];
                if (instances.TryGetValue(ancestor, out var above)
                    && above.Metadata.ScreenId.Equals(screen.Metadata.ScreenId))
                {
                    Issue("recursive_screen", path, screen.Metadata.ScreenId.Value, "A screen cannot occur beneath another instance of itself.");
                    break;
                }
            }
        }
        return new(issues.Count == 0, instances.Count,
            instances.Values.Select(s => s.Metadata.ScreenId.Value).Distinct(StringComparer.Ordinal).Count(), issues, gaps);
    }

    private static IEnumerable<SheetSymbol> Sheets(SchematicScreenData screen) =>
        screen.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>());
    private static bool ValidId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] KIID? id) => Guid.TryParseExact(id?.Value, "D", out var value)
        && value != Guid.Empty && id!.Value == value.ToString("D");
}
