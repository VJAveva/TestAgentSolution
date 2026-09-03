using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Models;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Verifies the shared row filter (used by report/email/summary exports) matches the grid's toggles so
/// a server-generated report reflects the user's active filters.
/// </summary>
public class RegressionRowFilterTests
{
    private static SubsystemRow Row(string comp, RegressionCategoryKind cat, params RegressionChangeRef[] changes) =>
        new(comp, comp, cat, RegressionEvidenceKind.Declared, [], 0, changes, "succeeded", [], [], 0, true);

    private static RegressionChangeRef Change(RegressionChangeKind kind, params RegressionWorkItemRef[] wi) =>
        new("id", "summary", DateTimeOffset.UtcNow, [], wi, kind);

    private static RegressionChangeRef ChangeWithSummary(string summary) =>
        new("id", summary, DateTimeOffset.UtcNow, [], [], RegressionChangeKind.PullRequest);

    [Fact]
    public void Apply_Should_HidePackageNoiseChanges_ByDefault_And_ShowAllChangesRestoresThem()
    {
        var rows = new[]
        {
            Row("A", RegressionCategoryKind.Runtime,
                ChangeWithSummary("Updated universal-packages.json"),
                ChangeWithSummary("Fix real bug")),
            Row("B", RegressionCategoryKind.Runtime,
                ChangeWithSummary("Updated Universal-Packages.json")),
        };

        var hidden = RegressionRowFilter.Apply(rows, new RegressionRowFilterOptions());
        Assert.Single(hidden); // A keeps its real change; B (noise-only) drops out
        Assert.Equal("A", hidden[0].Component);
        Assert.Equal("Fix real bug", Assert.Single(hidden[0].Changes).Summary);

        var all = RegressionRowFilter.Apply(rows, new RegressionRowFilterOptions { ShowAllChanges = true });
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Apply_Should_HideConfig_When_ShowConfigFalse()
    {
        var rows = new[]
        {
            Row("A", RegressionCategoryKind.Runtime, Change(RegressionChangeKind.Commit)),
            Row("B", RegressionCategoryKind.Config, Change(RegressionChangeKind.Commit)),
        };

        var result = RegressionRowFilter.Apply(rows, new RegressionRowFilterOptions { ShowConfig = false });

        Assert.Single(result);
        Assert.Equal("A", result[0].Component);
    }

    [Fact]
    public void Apply_Should_DropRow_When_HideAutomatedAndOnlyAutomatedChanges()
    {
        var rows = new[] { Row("A", RegressionCategoryKind.Runtime, Change(RegressionChangeKind.Automated)) };

        var result = RegressionRowFilter.Apply(rows, new RegressionRowFilterOptions { HideAutomated = true });

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_Should_KeepOnlyBugRows_When_FilterBug()
    {
        var bug = new RegressionWorkItemRef(1, RegressionWorkItemKind.Bug, "b", null);
        var rows = new[]
        {
            Row("A", RegressionCategoryKind.Runtime, Change(RegressionChangeKind.PullRequest, bug)),
            Row("B", RegressionCategoryKind.Runtime, Change(RegressionChangeKind.PullRequest)),
        };

        var result = RegressionRowFilter.Apply(rows, new RegressionRowFilterOptions { FilterBug = true });

        Assert.Single(result);
        Assert.Equal("A", result[0].Component);
    }

    [Fact]
    public void Apply_Should_KeepOnlySelectedComponent_When_ComponentSet()
    {
        var rows = new[]
        {
            Row("Alpha", RegressionCategoryKind.Runtime, Change(RegressionChangeKind.Commit)),
            Row("Beta", RegressionCategoryKind.Runtime, Change(RegressionChangeKind.Commit)),
        };

        var result = RegressionRowFilter.Apply(rows, new RegressionRowFilterOptions { Component = "Beta" });

        Assert.Single(result);
        Assert.Equal("Beta", result[0].Component);
    }
}
