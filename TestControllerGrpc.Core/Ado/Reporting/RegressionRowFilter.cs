using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>
/// Grid filter flags (Runtime/Config/Human-only/Bug/Story/IMS/component). Bound from the report/email/summary
/// query string so server-generated exports match what the user has filtered in the grid.
/// </summary>
public sealed class RegressionRowFilterOptions
{
    public bool ShowRuntime { get; set; } = true;
    public bool ShowConfig { get; set; } = true;
    public bool HideAutomated { get; set; }
    public bool ShowAllChanges { get; set; } // when false, Universal-Packages manifest changes are hidden
    public bool FilterBug { get; set; }
    public bool FilterStory { get; set; }
    public bool FilterIms { get; set; }
    public string? Component { get; set; }
}

/// <summary>
/// Applies the same row filtering the Regression grid uses, so exported/emailed reports match what the user
/// sees. Mirrors WPF <c>RegressionViewModel.ApplyFilter</c> and React <c>helpers.ts applyFilters</c>.
/// </summary>
public static class RegressionRowFilter
{
    public static IReadOnlyList<SubsystemRow> Apply(IReadOnlyList<SubsystemRow> rows, RegressionRowFilterOptions f)
    {
        IEnumerable<SubsystemRow> q = rows.Where(r =>
            r.Category is RegressionCategoryKind.Unclassified or RegressionCategoryKind.Both ||
            (r.Category == RegressionCategoryKind.Runtime && f.ShowRuntime) ||
            (r.Category == RegressionCategoryKind.Config && f.ShowConfig));

        if (!string.IsNullOrWhiteSpace(f.Component))
            q = q.Where(r => string.Equals(r.Component, f.Component, StringComparison.OrdinalIgnoreCase));

        if (f.HideAutomated)
            q = q.Select(StripAutomated).Where(r => r.Changes.Count > 0);

        // Hide Universal-Packages manifest bumps by default; "All changes" (ShowAllChanges) restores them.
        if (!f.ShowAllChanges)
            q = q.Select(StripPackageNoise).Where(r => r.Changes.Count > 0);

        if (f.FilterBug || f.FilterStory || f.FilterIms)
            q = q.Where(r =>
            {
                var kinds = r.Changes.SelectMany(c => c.WorkItems).Select(w => w.Kind).ToHashSet();
                return (f.FilterBug && kinds.Contains(RegressionWorkItemKind.Bug)) ||
                       (f.FilterStory && kinds.Contains(RegressionWorkItemKind.Story)) ||
                       (f.FilterIms && kinds.Contains(RegressionWorkItemKind.Ims));
            });

        return q.ToList();
    }

    /// <summary>True when a change's subject is dependency/pipeline noise (Universal-Packages manifest or a YAML pipeline file).</summary>
    public static bool IsPackageNoiseChange(RegressionChangeRef change)
    {
        var s = change.Summary;
        return !string.IsNullOrEmpty(s)
            && (s.Contains("universal-package", StringComparison.OrdinalIgnoreCase)
                || s.Contains(".yml", StringComparison.OrdinalIgnoreCase)
                || s.Contains(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    private static SubsystemRow StripPackageNoise(SubsystemRow row)
    {
        var kept = row.Changes.Where(c => !IsPackageNoiseChange(c)).ToList();
        // Package-noise files are already dropped by the collector, so FilesModified needs no recompute here.
        return kept.Count == row.Changes.Count ? row : row with { Changes = kept };
    }

    private static SubsystemRow StripAutomated(SubsystemRow row)
    {
        var human = row.Changes.Where(c => c.Kind != RegressionChangeKind.Automated).ToList();
        if (human.Count == row.Changes.Count)
            return row;
        var files = human.SelectMany(c => c.FilePaths).Distinct().ToList();
        return row with { Changes = human, FilesModified = files.Take(25).ToList(), TotalFilesModified = files.Count };
    }
}
