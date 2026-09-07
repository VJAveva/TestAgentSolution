using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>One work item shown as a row, with the changes attributed to it.</summary>
public sealed record CodeChurnWorkItemEntry(
    RegressionWorkItemRef WorkItem,
    IReadOnlyList<RegressionChangeRef> Changes);

/// <summary>
/// A Feature and everything rolled up under it. <see cref="FeatureId"/> is null for the orphan bucket.
/// </summary>
public sealed record CodeChurnFeatureGroup(
    int? FeatureId,
    string FeatureTitle,
    IReadOnlyList<CodeChurnWorkItemEntry> WorkItems,
    IReadOnlyList<RegressionChangeRef> Changes)
{
    public int WorkItemCount => WorkItems.Count;
    public int ChangeCount => Changes.Count;
    public int FileCount => Changes.SelectMany(c => c.FilePaths).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool IsOrphanBucket => FeatureId is null;
}

/// <summary>What one excluded type cost the report, so the suppression is visible rather than inferred.</summary>
public sealed record CodeChurnExclusion(string Type, int SuppressedWorkItems, int ReattributedChanges);

/// <summary>
/// The one shape the email, Excel and export renderers all read. Filtering, grouping and roll-up happen here,
/// once — three renderers doing it themselves is why they disagreed.
/// </summary>
public sealed record CodeChurnReportModel(
    ChurnReport Source,
    IReadOnlyList<CodeChurnFeatureGroup> FeatureGroups,
    CodeChurnFeatureGroup OrphanGroup,
    IReadOnlyList<CodeChurnExclusion> Exclusions,
    IReadOnlyList<RegressionChangeRef> CoverageGaps)
{
    /// <summary>Feature groups with the orphan bucket last, which is the render order for every output.</summary>
    public IReadOnlyList<CodeChurnFeatureGroup> GroupsForDisplay => [.. FeatureGroups, OrphanGroup];

    /// <summary>
    /// Required in all three outputs. Suppressed data that is invisible is indistinguishable from data that
    /// was never collected.
    /// </summary>
    public string ExclusionFooter
    {
        get
        {
            if (Exclusions.Count == 0 && CoverageGaps.Count == 0)
                return "Excluded by policy: none.";

            var parts = Exclusions
                .Where(e => e.SuppressedWorkItems > 0)
                .Select(e => e.ReattributedChanges > 0
                    ? $"{e.SuppressedWorkItems} {e.Type} work item(s) ({e.ReattributedChanges} change(s) re-attributed to parents)"
                    : $"{e.SuppressedWorkItems} {e.Type} work item(s)")
                .ToList();

            string text = parts.Count == 0
                ? "Excluded by policy: none."
                : "Excluded by policy: " + string.Join("; ", parts) + ".";

            if (CoverageGaps.Count > 0)
                text += $" {CoverageGaps.Count} change(s) had no linked work item.";

            return text;
        }
    }
}

public interface ICodeChurnReportBuilder
{
    Task<CodeChurnReportModel> BuildAsync(ChurnReport report, CancellationToken ct);
}
