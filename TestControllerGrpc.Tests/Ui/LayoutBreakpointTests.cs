using System.IO;
using System.Text.RegularExpressions;
using TestControllerGrpc.Views.Behaviors;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-8 adaptive layout. WPF has no media queries, so the width bands live in one behavior and views
/// react with triggers. Classification is pure arithmetic and is tested directly; the wiring is checked
/// by file analysis so no WPF element has to be instantiated.
/// </summary>
public sealed class LayoutBreakpointTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Theory]
    [InlineData(1920, Breakpoint.Wide)]
    [InlineData(1600, Breakpoint.Wide)]
    [InlineData(1599, Breakpoint.Standard)]
    [InlineData(1366, Breakpoint.Standard)]
    [InlineData(1365, Breakpoint.Compact)]
    [InlineData(1024, Breakpoint.Compact)]
    [InlineData(1023, Breakpoint.Narrow)]
    [InlineData(800, Breakpoint.Narrow)]
    public void Classify_Should_MatchSpecBands_When_GivenAWidth(double width, Breakpoint expected) =>
        Assert.Equal(expected, LayoutBreakpoint.Classify(width));

    /// <summary>Boundaries are inclusive at the bottom of each band; an off-by-one here silently
    /// shifts every adaptive rule in the app.</summary>
    [Fact]
    public void Bands_Should_BeContiguous_When_WalkedAcrossBoundaries()
    {
        Assert.NotEqual(LayoutBreakpoint.Classify(LayoutBreakpoint.WideMin),
                        LayoutBreakpoint.Classify(LayoutBreakpoint.WideMin - 1));
        Assert.NotEqual(LayoutBreakpoint.Classify(LayoutBreakpoint.StandardMin),
                        LayoutBreakpoint.Classify(LayoutBreakpoint.StandardMin - 1));
        Assert.NotEqual(LayoutBreakpoint.Classify(LayoutBreakpoint.CompactMin),
                        LayoutBreakpoint.Classify(LayoutBreakpoint.CompactMin - 1));
    }

    [Fact]
    public void FleetView_Should_OptIntoBreakpoints_When_ItHasSecondaryKpis()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "AgentWorkspace", "FleetView.xaml"));

        Assert.Contains("LayoutBreakpoint.IsEnabled=\"True\"", xaml);
        Assert.True(Regex.Matches(xaml, "KpiCardSecondaryStyle").Count >= 2,
            "The least critical KPIs should yield before the strip clips.");
    }

    /// <summary>A style that collapses at a breakpoint is useless if nothing raises the breakpoint.</summary>
    [Fact]
    public void SecondaryKpiStyle_Should_CollapseBelowStandard_When_SpaceIsTight()
    {
        var controls = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "Styles", "ControlStyles.xaml"));

        var block = Regex.Match(controls, "<Style x:Key=\"KpiCardSecondaryStyle\".*?</Style>", RegexOptions.Singleline);
        Assert.True(block.Success, "KpiCardSecondaryStyle is missing.");

        foreach (var band in new[] { "Compact", "Narrow" })
            Assert.Contains($"Value=\"{band}\"", block.Value);
    }

    [Theory]
    [InlineData(Breakpoint.Wide)]
    [InlineData(Breakpoint.Standard)]
    public void PanesFor_Should_HonourIntentExactly_When_ThereIsRoom(Breakpoint band)
    {
        var intent = (Tree: true, Agent: true, Log: true);

        Assert.Equal(intent, LayoutBreakpoint.PanesFor(intent, band));
    }

    [Fact]
    public void PanesFor_Should_DropTheAgentPaneFirst_When_Compact()
    {
        var panes = LayoutBreakpoint.PanesFor((Tree: true, Agent: true, Log: true), Breakpoint.Compact);

        Assert.True(panes.Tree);
        Assert.False(panes.Agent);
    }

    [Fact]
    public void PanesFor_Should_DropBothSidePanes_When_Narrow()
    {
        var panes = LayoutBreakpoint.PanesFor((Tree: true, Agent: true, Log: true), Breakpoint.Narrow);

        Assert.False(panes.Tree);
        Assert.False(panes.Agent);
    }

    /// <summary>
    /// The whole point of keeping intent separate: shrinking then re-widening must give the user their
    /// layout back, and must never silently re-open a pane they had deliberately closed.
    /// </summary>
    [Fact]
    public void PanesFor_Should_RoundTripIntent_When_WindowShrinksAndGrowsAgain()
    {
        var intent = (Tree: true, Agent: false, Log: true);

        var squeezed = LayoutBreakpoint.PanesFor(intent, Breakpoint.Narrow);
        Assert.False(squeezed.Tree);

        var restored = LayoutBreakpoint.PanesFor(intent, Breakpoint.Wide);
        Assert.Equal(intent, restored);
        Assert.False(restored.Agent);
    }

    /// <summary>Stats the operator is watching should reflow, not disappear.</summary>
    [Fact]
    public void ExecutionDashboard_Should_WrapStatsRatherThanHideThem_When_Narrow()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "ExecutionDashboardWindow.xaml"));

        Assert.Contains("LayoutBreakpoint.IsEnabled=\"True\"", xaml);

        var style = Regex.Match(xaml, "<UniformGrid.Style>.*?</UniformGrid.Style>", RegexOptions.Singleline);
        Assert.True(style.Success, "The stats bar should adapt its row count.");
        Assert.Contains("Value=\"Narrow\"", style.Value);
        Assert.Contains("Property=\"Rows\" Value=\"2\"", style.Value);
    }
}
