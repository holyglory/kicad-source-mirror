using System.ComponentModel;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record ConnectedPlacementProposal(string DocumentJson, IReadOnlyList<string> SymbolIds,
    long DeltaXNm, long DeltaYNm);

public sealed record PlacementPlanToolResult(string? ReconciledEngineeringXml,
    IReadOnlyList<ConnectedPlacementProposal> Moves, IReadOnlyList<PlacementPlanIssue> Issues,
    IReadOnlyList<SchematicProjectionConflict> Conflicts, IReadOnlyList<SchematicBindingIssue> BindingIssues,
    bool UnprojectedSnapshotChanges, IReadOnlyList<HierarchyCoverageGap> CoverageGaps,
    string? ErrorCode, string? ErrorMessage);

[McpServerToolType]
public sealed class PlacementTools
{
    [McpServerTool(Name = "kicad_design_plan_placement", ReadOnly = true, UseStructuredContent = true),
     Description("Prepare connected-symbol translations from supplied baseline design:1 XML, desired engineering-design:1 XML, observed typed hierarchy XML and declared knowledge libraries. Uses exact sheet paths and native symbol identities; repeated-sheet geometry is moved once, and symbols with equal displacements on the same sheet form one selection. Preserves coordinate-free intent, conflicts, native coverage gaps and unprojected changes. Rotation, mirroring, lock changes and electrical changes are not translated into moves. Returns proposals only: does not access editors, verify live revisions, apply edits, advance synchronization or certify connectivity. Native application still requires a fresh explicit instance/document revision and operation ID; currently only displayed sheets can be moved.")]
    public PlacementPlanToolResult PlanPlacement(string baselineDesignXml, string desiredEngineeringXml,
        string observedHierarchyXml, string[] knowledgeLibraryXml, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var libraries = new List<ComponentKnowledgeLibrary>();
            foreach (string xml in knowledgeLibraryXml)
            {
                cancellationToken.ThrowIfCancellationRequested();
                libraries.Add(ComponentKnowledgeXml.ReadLibrary(xml));
            }
            var baseline = SchematicDesignXml.Read(baselineDesignXml, libraries);
            var desired = EngineeringDesignXml.Read(desiredEngineeringXml, libraries);
            var observed = SchematicDataXml.Read(observedHierarchyXml) as SchematicHierarchyData
                ?? throw new AutomationException("invalid_design_xml", "Observed data must be a typed schematic hierarchy.");
            var result = SchematicPlacementPlan.Plan(baseline, desired, observed, libraries, cancellationToken);
            var moves = new List<ConnectedPlacementProposal>();
            foreach (var operation in result.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                moves.Add(new(SchematicJson.Formatter.Format(operation.TargetDocument),
                    operation.MoveConnectedSymbols.Symbols.Select(s => s.Value).ToArray(),
                    operation.MoveConnectedSymbols.Delta.XNm, operation.MoveConnectedSymbols.Delta.YNm));
            }
            return new(result.ReconciledModel is null ? null : EngineeringDesignXml.Write(result.ReconciledModel, libraries),
                moves, result.Issues, result.Conflicts, result.BindingIssues, result.UnprojectedSnapshotChanges,
                result.CoverageGaps, null, null);
        }
        catch (AutomationException error)
        {
            return new(null, [], [], [], [], true, [], error.Code, error.Message);
        }
    }
}
