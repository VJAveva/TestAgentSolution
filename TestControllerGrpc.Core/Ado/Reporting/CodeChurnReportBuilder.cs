using TestControllerGrpc.Models;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>
/// Applies <see cref="CodeChurnWorkItemPolicy"/> and rolls changes up to their Feature.
/// </summary>
/// <remarks>
/// Hierarchy resolution happens here, once, so the renderers stay pure and synchronous. Doing it during
/// rendering would put ADO round-trips behind three different output paths.
///
/// Re-attribution, not filtering: a change whose only link is an excluded type is moved to that work item's
/// nearest non-excluded ancestor. It is never dropped. A change with no work item at all lands in
/// <see cref="CodeChurnReportModel.CoverageGaps"/> rather than disappearing.
/// </remarks>
public sealed class CodeChurnReportBuilder : ICodeChurnReportBuilder
{
    private readonly IWorkItemHierarchyResolver _hierarchy;
    private readonly CodeChurnWorkItemPolicy _policy;

    public CodeChurnReportBuilder(IWorkItemHierarchyResolver hierarchy, CodeChurnWorkItemPolicy policy)
    {
        _hierarchy = hierarchy;
        _policy = policy;
    }

    public async Task<CodeChurnReportModel> BuildAsync(ChurnReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<RegressionChangeRef> allChanges = report.Rows
            .SelectMany(r => r.Changes)
            .GroupBy(c => c.ChangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        int[] workItemIds = allChanges
            .SelectMany(c => c.WorkItems)
            .Select(w => w.Id)
            .Distinct()
            .ToArray();

        WorkItemHierarchy hierarchy = _policy.RollUpToFeature && workItemIds.Length > 0
            ? await _hierarchy.ResolveAsync(workItemIds, ct).ConfigureAwait(false)
            : new WorkItemHierarchy(new Dictionary<int, IReadOnlyList<WorkItemNode>>(), new HashSet<int>());

        var groups = new Dictionary<int, GroupAccumulator>();
        var orphan = new GroupAccumulator(null, _policy.OrphanBucketName);
        var coverageGaps = new List<RegressionChangeRef>();
        var suppressed = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var reattributed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (RegressionChangeRef change in allChanges)
        {
            if (change.WorkItems.Count == 0)
            {
                coverageGaps.Add(change);
                continue;
            }

            var placed = false;

            foreach (RegressionWorkItemRef workItem in change.WorkItems)
            {
                bool excluded = _policy.IsExcluded(workItem);
                if (excluded)
                {
                    string type = workItem.WorkItemType ?? "Unknown";
                    (suppressed.TryGetValue(type, out HashSet<int>? ids) ? ids : suppressed[type] = []).Add(workItem.Id);
                }

                RegressionWorkItemRef? row = excluded
                    ? NearestIncludedAncestor(workItem, hierarchy)
                    : workItem;

                if (excluded)
                {
                    string type = workItem.WorkItemType ?? "Unknown";
                    (reattributed.TryGetValue(type, out HashSet<string>? changes) ? changes : reattributed[type] = [])
                        .Add(change.ChangeId);
                }

                // The excluded item had no surviving ancestor: keep the change visible in the orphan bucket
                // rather than losing it, which is the whole point of the policy.
                if (row is null)
                {
                    orphan.Add(workItem, change, countWorkItem: false);
                    placed = true;
                    continue;
                }

                WorkItemNode? feature = NearestFeature(row.Id, hierarchy);
                if (feature is null)
                {
                    orphan.Add(row, change, countWorkItem: true);
                }
                else
                {
                    if (!groups.TryGetValue(feature.Id, out GroupAccumulator? group))
                        groups[feature.Id] = group = new GroupAccumulator(feature.Id, feature.Title);
                    group.Add(row, change, countWorkItem: true);
                }

                placed = true;
            }

            if (!placed)
                coverageGaps.Add(change);
        }

        var exclusions = suppressed
            .Select(kv => new CodeChurnExclusion(
                kv.Key,
                kv.Value.Count,
                reattributed.TryGetValue(kv.Key, out HashSet<string>? c) ? c.Count : 0))
            .OrderBy(e => e.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<CodeChurnFeatureGroup> featureGroups = groups.Values
            .Select(g => g.ToGroup())
            .OrderByDescending(g => g.ChangeCount)
            .ThenBy(g => g.FeatureTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Always present, even at zero, so "no orphans" reads as a measured zero rather than an omission.
        return new CodeChurnReportModel(report, featureGroups, orphan.ToGroup(), exclusions, coverageGaps);
    }

    private RegressionWorkItemRef? NearestIncludedAncestor(RegressionWorkItemRef excluded, WorkItemHierarchy hierarchy)
    {
        if (!hierarchy.AncestorsById.TryGetValue(excluded.Id, out IReadOnlyList<WorkItemNode>? chain))
            return null;

        foreach (WorkItemNode node in chain)
        {
            var candidate = new RegressionWorkItemRef(
                node.Id, RegressionWorkItemKind.Other, node.Title, null, null, node.WorkItemType);

            if (!_policy.IsExcluded(candidate))
                return candidate;
        }

        return null;
    }

    private static WorkItemNode? NearestFeature(int id, WorkItemHierarchy hierarchy)
    {
        if (!hierarchy.AncestorsById.TryGetValue(id, out IReadOnlyList<WorkItemNode>? chain))
            return null;

        return chain.FirstOrDefault(n =>
            string.Equals(n.WorkItemType, "Feature", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class GroupAccumulator
    {
        private readonly Dictionary<int, List<RegressionChangeRef>> _byWorkItem = [];
        private readonly Dictionary<int, RegressionWorkItemRef> _workItems = [];
        private readonly Dictionary<string, RegressionChangeRef> _changes = new(StringComparer.OrdinalIgnoreCase);

        public GroupAccumulator(int? featureId, string title)
        {
            FeatureId = featureId;
            Title = title;
        }

        public int? FeatureId { get; }
        public string Title { get; }

        public void Add(RegressionWorkItemRef workItem, RegressionChangeRef change, bool countWorkItem)
        {
            _changes[change.ChangeId] = change;
            if (!countWorkItem) return;

            _workItems[workItem.Id] = workItem;
            if (!_byWorkItem.TryGetValue(workItem.Id, out List<RegressionChangeRef>? list))
                _byWorkItem[workItem.Id] = list = [];
            if (!list.Any(c => string.Equals(c.ChangeId, change.ChangeId, StringComparison.OrdinalIgnoreCase)))
                list.Add(change);
        }

        public CodeChurnFeatureGroup ToGroup() => new(
            FeatureId,
            Title,
            _workItems.Values
                .Select(w => new CodeChurnWorkItemEntry(w, _byWorkItem.GetValueOrDefault(w.Id) ?? []))
                .OrderBy(e => e.WorkItem.Id)
                .ToList(),
            _changes.Values.OrderBy(c => c.ObservedUtc).ToList());
    }
}
