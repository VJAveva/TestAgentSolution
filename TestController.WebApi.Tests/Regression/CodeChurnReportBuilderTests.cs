using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Workstream A / P21. The rule under test: excluding a type removes it as a ROW, never as a CHANGE.
/// A commit whose only link is a Task must still appear, attributed to the Task's nearest surviving ancestor.
/// </summary>
public class CodeChurnReportBuilderTests
{
    private sealed class FakeHierarchy : IWorkItemHierarchyResolver
    {
        private readonly Dictionary<int, IReadOnlyList<WorkItemNode>> _ancestors;
        public FakeHierarchy(Dictionary<int, IReadOnlyList<WorkItemNode>> ancestors) => _ancestors = ancestors;

        public Task<WorkItemHierarchy> ResolveAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
        {
            var orphans = ids.Where(id =>
                !_ancestors.TryGetValue(id, out var chain) ||
                !chain.Any(n => string.Equals(n.WorkItemType, "Feature", StringComparison.OrdinalIgnoreCase)))
                .ToHashSet();

            return Task.FromResult(new WorkItemHierarchy(_ancestors, orphans));
        }
    }

    private static RegressionWorkItemRef Wi(int id, string type) =>
        new(id, RegressionWorkItemKind.Other, $"WI {id}", null, null, type);

    private static RegressionChangeRef Change(string id, params RegressionWorkItemRef[] workItems) =>
        new(id, $"change {id}", DateTimeOffset.UtcNow, ["src/a.cs"], workItems);

    private static SubsystemRow Row(string component, params RegressionChangeRef[] changes) =>
        new(
            Component: component,
            Subsystem: component,
            Category: RegressionCategoryKind.Runtime,
            CategoryConfidence: RegressionEvidenceKind.Declared,
            FilesModified: [],
            TotalFilesModified: changes.SelectMany(c => c.FilePaths).Distinct().Count(),
            Changes: changes,
            RiskTier: "succeeded",
            AutomatedSuites: [],
            ManualSuites: [],
            EstimatedMinutes: 0,
            IsEstimate: true);

    private static ChurnReport Report(params RegressionChangeRef[] changes) =>
        new("Build", "range", default, default, DateTimeOffset.UtcNow, [Row("AAMxCore", changes)]);

    private static CodeChurnReportBuilder Build(
        Dictionary<int, IReadOnlyList<WorkItemNode>> ancestors, CodeChurnWorkItemPolicy? policy = null) =>
        new(new FakeHierarchy(ancestors), policy ?? new CodeChurnWorkItemPolicy());

    [Fact]
    public async Task BuildAsync_Should_KeepChange_When_OnlyLinkIsExcludedTask()
    {
        // Task 10 -> Story 20 -> Feature 30. The Task is suppressed; the change must survive on the Story.
        var ancestors = new Dictionary<int, IReadOnlyList<WorkItemNode>>
        {
            [10] = [new WorkItemNode(20, "Story 20", "User Story"), new WorkItemNode(30, "Feature 30", "Feature")],
            [20] = [new WorkItemNode(30, "Feature 30", "Feature")],
        };

        CodeChurnReportModel model = await Build(ancestors)
            .BuildAsync(Report(Change("c1", Wi(10, "Task"))), CancellationToken.None);

        CodeChurnFeatureGroup feature = Assert.Single(model.FeatureGroups);
        Assert.Equal(30, feature.FeatureId);
        Assert.Equal(1, feature.ChangeCount);

        // Re-attributed to the parent Story, and the Task itself is not a row.
        CodeChurnWorkItemEntry entry = Assert.Single(feature.WorkItems);
        Assert.Equal(20, entry.WorkItem.Id);
        Assert.Empty(model.CoverageGaps);
    }

    [Fact]
    public async Task BuildAsync_Should_ReportSuppressionAndReattribution()
    {
        var ancestors = new Dictionary<int, IReadOnlyList<WorkItemNode>>
        {
            [10] = [new WorkItemNode(20, "Story 20", "User Story"), new WorkItemNode(30, "Feature 30", "Feature")],
            [20] = [new WorkItemNode(30, "Feature 30", "Feature")],
        };

        CodeChurnReportModel model = await Build(ancestors)
            .BuildAsync(Report(Change("c1", Wi(10, "Task"))), CancellationToken.None);

        CodeChurnExclusion exclusion = Assert.Single(model.Exclusions);
        Assert.Equal("Task", exclusion.Type);
        Assert.Equal(1, exclusion.SuppressedWorkItems);
        Assert.Equal(1, exclusion.ReattributedChanges);
        Assert.Contains("Task", model.ExclusionFooter);
        Assert.Contains("re-attributed", model.ExclusionFooter);
    }

    [Fact]
    public async Task BuildAsync_Should_KeepChangeInOrphanBucket_When_ExcludedItemHasNoAncestor()
    {
        // Worst case: only link is a Task with no parent at all. Still must not vanish.
        CodeChurnReportModel model = await Build([])
            .BuildAsync(Report(Change("c1", Wi(10, "Task"))), CancellationToken.None);

        Assert.Equal(1, model.OrphanGroup.ChangeCount);
        Assert.Empty(model.CoverageGaps);
    }

    [Fact]
    public async Task BuildAsync_Should_ListChangeAsCoverageGap_When_NoWorkItems()
    {
        CodeChurnReportModel model = await Build([])
            .BuildAsync(Report(Change("c1")), CancellationToken.None);

        Assert.Single(model.CoverageGaps);
        Assert.Contains("no linked work item", model.ExclusionFooter);
    }

    [Fact]
    public async Task BuildAsync_Should_AlwaysIncludeOrphanGroup_When_Empty()
    {
        var ancestors = new Dictionary<int, IReadOnlyList<WorkItemNode>>
        {
            [20] = [new WorkItemNode(30, "Feature 30", "Feature")],
        };

        CodeChurnReportModel model = await Build(ancestors)
            .BuildAsync(Report(Change("c1", Wi(20, "User Story"))), CancellationToken.None);

        // Present with a visible zero rather than absent and ambiguous.
        Assert.NotNull(model.OrphanGroup);
        Assert.Equal(0, model.OrphanGroup.ChangeCount);
        Assert.Equal(model.OrphanGroup, model.GroupsForDisplay[^1]);
    }

    [Fact]
    public async Task BuildAsync_Should_GroupUnparentedStoryIntoOrphanBucket()
    {
        CodeChurnReportModel model = await Build([])
            .BuildAsync(Report(Change("c1", Wi(20, "User Story"))), CancellationToken.None);

        Assert.Empty(model.FeatureGroups);
        Assert.Equal(1, model.OrphanGroup.ChangeCount);
        Assert.Equal("Unassigned to Feature", model.OrphanGroup.FeatureTitle);
    }

    [Fact]
    public async Task BuildAsync_Should_NotDoubleCountChange_When_SharedAcrossRows()
    {
        var ancestors = new Dictionary<int, IReadOnlyList<WorkItemNode>>
        {
            [20] = [new WorkItemNode(30, "Feature 30", "Feature")],
        };
        RegressionChangeRef shared = Change("c1", Wi(20, "User Story"));

        var report = new ChurnReport("Build", "r", default, default, DateTimeOffset.UtcNow,
            [Row("A", shared), Row("B", shared)]);

        CodeChurnReportModel model = await Build(ancestors).BuildAsync(report, CancellationToken.None);

        Assert.Equal(1, Assert.Single(model.FeatureGroups).ChangeCount);
    }

    [Fact]
    public async Task BuildAsync_Should_SkipHierarchy_When_RollUpDisabled()
    {
        var policy = new CodeChurnWorkItemPolicy { RollUpToFeature = false };

        CodeChurnReportModel model = await Build([], policy)
            .BuildAsync(Report(Change("c1", Wi(20, "User Story"))), CancellationToken.None);

        Assert.Equal(1, model.OrphanGroup.ChangeCount);
    }
}
