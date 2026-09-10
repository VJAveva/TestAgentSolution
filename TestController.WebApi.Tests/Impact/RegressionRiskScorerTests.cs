using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Risk;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Impact;

public sealed class RegressionRiskScorerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    private static RegressionRiskScorer Scorer(ImpactMappingOptions? options = null)
        => new(Options.Create(options ?? new ImpactMappingOptions()));

    private static SubsystemRow Row(
        int files = 1,
        int changes = 1,
        int bugs = 0,
        int incidents = 0,
        double daysOld = 1,
        string buildResult = "succeeded",
        bool declared = true,
        RegressionCategoryKind category = RegressionCategoryKind.Runtime)
    {
        var workItems = new List<RegressionWorkItemRef>();
        int id = 1;
        for (int i = 0; i < bugs; i++)
            workItems.Add(new RegressionWorkItemRef(id++, RegressionWorkItemKind.Bug, "bug", null));
        for (int i = 0; i < incidents; i++)
            workItems.Add(new RegressionWorkItemRef(id++, RegressionWorkItemKind.Ims, "ims", null));

        List<RegressionChangeRef> changeRefs = Enumerable.Range(0, changes)
            .Select(i => new RegressionChangeRef(
                $"c{i}", $"summary {i}", Now.AddDays(-daysOld), [], i == 0 ? workItems : []))
            .ToList();

        return new SubsystemRow(
            Component: "AAMxCore",
            Subsystem: "Core",
            Category: category,
            CategoryConfidence: RegressionEvidenceKind.Declared,
            FilesModified: [],
            TotalFilesModified: files,
            Changes: changeRefs,
            RiskTier: buildResult,
            AutomatedSuites: [],
            ManualSuites: [],
            EstimatedMinutes: 0,
            IsEstimate: true,
            RegressionAreas: declared ? ["Field Reference"] : [],
            BuildResult: buildResult);
    }

    [Fact]
    public void Score_Should_ReturnMedium_When_ComponentIsQuiet()
    {
        RegressionRiskAssessment result = Scorer().Score(
            Row(files: 3, changes: 1, daysOld: 20), Now);

        Assert.Equal(RiskTier.Medium, result.Tier);
    }

    [Fact]
    public void Score_Should_ReturnCritical_When_ChurnDefectsAndFailedBuildCombine()
    {
        RegressionRiskAssessment result = Scorer().Score(
            Row(files: 80, changes: 20, bugs: 2, incidents: 2, daysOld: 0, buildResult: "failed"), Now);

        Assert.Equal(RiskTier.Critical, result.Tier);
    }

    [Fact]
    public void Score_Should_NeverReturnUnmapped_When_NothingIsKnown()
    {
        // Unmapped sorts below Medium and both consumers treat it as the weakest tier, so an area we
        // cannot classify must not land there.
        RegressionRiskAssessment result = Scorer().Score(
            Row(files: 0, changes: 0, daysOld: 999, declared: false,
                category: RegressionCategoryKind.Unclassified), Now);

        Assert.Equal(RiskTier.Medium, result.Tier);
    }

    [Fact]
    public void Score_Should_RaiseScore_When_CoverageIsUncertain()
    {
        SubsystemRow mapped = Row(declared: true);
        SubsystemRow unmapped = Row(declared: false);

        Assert.True(Scorer().Score(unmapped, Now).Score > Scorer().Score(mapped, Now).Score);
    }

    [Fact]
    public void Score_Should_CountWorkItemOnce_When_LinkedToManyChanges()
    {
        var workItem = new RegressionWorkItemRef(7, RegressionWorkItemKind.Bug, "bug", null);
        List<RegressionChangeRef> changes = Enumerable.Range(0, 5)
            .Select(i => new RegressionChangeRef($"c{i}", "s", Now.AddDays(-1), [], [workItem]))
            .ToList();
        SubsystemRow row = Row() with { Changes = changes };

        double defects = Scorer().Score(row, Now).Components.Single(c => c.Name == "defects").Raw;

        Assert.Equal(0.25, defects, 3);
    }

    [Fact]
    public void Score_Should_WeightIncidentsDoubleBugs()
    {
        double withBug = Scorer().Score(Row(bugs: 1), Now).Components.Single(c => c.Name == "defects").Raw;
        double withIncident = Scorer().Score(Row(incidents: 1), Now).Components.Single(c => c.Name == "defects").Raw;

        Assert.Equal(withBug * 2, withIncident, 3);
    }

    [Fact]
    public void Score_Should_DecayRecency_When_ChangeIsOlderThanHalfLife()
    {
        double fresh = Scorer().Score(Row(daysOld: 0), Now).Components.Single(c => c.Name == "recency").Raw;
        double stale = Scorer().Score(Row(daysOld: 7), Now).Components.Single(c => c.Name == "recency").Raw;

        Assert.Equal(1.0, fresh, 3);
        Assert.Equal(0.5, stale, 3);
    }

    [Fact]
    public void Score_Should_PopulateChurnMetrics_When_RowHasChanges()
    {
        ChurnMetrics churn = Scorer().Score(Row(files: 12, changes: 4, daysOld: 2), Now).Churn;

        Assert.Equal(12, churn.FilesTouched);
        Assert.Equal(4, churn.CommitCount);
        Assert.Equal(Now.AddDays(-2), churn.LastChangedUtc);
    }

    [Fact]
    public void Score_Should_TreatUnknownBuildResultAsFailure()
    {
        double raw = Scorer().Score(Row(buildResult: "unknown"), Now)
            .Components.Single(c => c.Name == "buildFailure").Raw;

        Assert.Equal(1, raw);
    }

    [Fact]
    public void Score_Should_StayWithinUnitInterval_When_EverySignalIsMaximal()
    {
        RegressionRiskAssessment result = Scorer().Score(
            Row(files: 5000, changes: 900, bugs: 50, incidents: 50, daysOld: 0,
                buildResult: "failed", declared: false, category: RegressionCategoryKind.Unclassified),
            Now);

        Assert.InRange(result.Score, 0, 1);
        Assert.Equal(RiskTier.Critical, result.Tier);
    }
}
