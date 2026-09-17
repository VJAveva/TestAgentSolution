using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels.Regression;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// The impacted-test-case section is collapsible and capped at the top few. These pin that logic so a
/// view-layer change cannot quietly break it: the grid renders <c>VisibleTestMatches</c>, not
/// <c>TestMatches</c>, and both controls are command-driven rather than IsChecked-bound.
/// </summary>
public sealed class SubsystemRowMatchesTests
{
    private static SubsystemRow Row() => new(
        Component: "AAMxCore",
        Subsystem: "Core",
        Category: RegressionCategoryKind.Runtime,
        CategoryConfidence: RegressionEvidenceKind.Declared,
        FilesModified: [],
        TotalFilesModified: 0,
        Changes: [],
        RiskTier: "succeeded",
        AutomatedSuites: [],
        ManualSuites: [],
        EstimatedMinutes: 0,
        IsEstimate: true);

    private static SubsystemRowViewModel VmWithMatches(int count)
    {
        var vm = new SubsystemRowViewModel(Row());
        for (var i = 1; i <= count; i++)
            vm.TestMatches.Add(new ImpactedTestCaseRow(
                1000 + i, $"Verify scenario {i}", $"https://dev.azure.test/_workitems/edit/{1000 + i}",
                "#42", "full", "64%", "Directly linked to a work item changed in this build",
                $"Description {i}"));
        return vm;
    }

    [Fact]
    public void ToggleShowAllMatches_Should_RevealEveryMatch_When_Invoked()
    {
        var vm = VmWithMatches(149);

        vm.ToggleShowAllMatchesCommand.Execute(null);

        Assert.True(vm.ShowAllMatches);
        Assert.Equal(149, vm.VisibleTestMatches.Count);
    }

    [Fact]
    public void ToggleShowAllMatches_Should_ReturnToTopFive_When_InvokedTwice()
    {
        var vm = VmWithMatches(149);

        vm.ToggleShowAllMatchesCommand.Execute(null);
        vm.ToggleShowAllMatchesCommand.Execute(null);

        Assert.False(vm.ShowAllMatches);
        Assert.Equal(5, vm.VisibleTestMatches.Count);
    }

    [Fact]
    public void MoreMatchesText_Should_DescribeTheNextAction_When_Toggled()
    {
        var vm = VmWithMatches(149);
        Assert.Equal("Show all 149", vm.MoreMatchesText);

        vm.ToggleShowAllMatchesCommand.Execute(null);

        Assert.Equal("Show top 5", vm.MoreMatchesText);
    }

    [Fact]
    public void ToggleMatchesExpanded_Should_CollapseAndReExpand_When_Invoked()
    {
        var vm = VmWithMatches(10);
        Assert.True(vm.AreMatchesExpanded);       // sections start open

        vm.ToggleMatchesExpandedCommand.Execute(null);
        Assert.False(vm.AreMatchesExpanded);

        vm.ToggleMatchesExpandedCommand.Execute(null);
        Assert.True(vm.AreMatchesExpanded);
    }

    [Fact]
    public void MatchesChevron_Should_FollowExpansionState_When_Toggled()
    {
        var vm = VmWithMatches(10);
        var expanded = vm.MatchesChevron;

        vm.ToggleMatchesExpandedCommand.Execute(null);

        // The chevron is the only visual cue that the click registered.
        Assert.NotEqual(expanded, vm.MatchesChevron);
    }

    [Fact]
    public void HasMoreMatches_Should_BeFalse_When_FiveOrFewerMatched()
    {
        Assert.False(VmWithMatches(5).HasMoreMatches);
        Assert.True(VmWithMatches(6).HasMoreMatches);
    }

    [Fact]
    public void TestMatchesHeader_Should_ReportTotal_NotVisibleCount()
    {
        var vm = VmWithMatches(149);

        vm.ToggleShowAllMatchesCommand.Execute(null);
        vm.ToggleShowAllMatchesCommand.Execute(null);   // back to top 5

        Assert.Equal(5, vm.VisibleTestMatches.Count);
        Assert.Equal("Impacted test cases (149)", vm.TestMatchesHeader);
    }
}
