using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels.Regression;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// The Manual Suite column renders bare work-item ids until these projections are correct: an id is not a
/// test case name, and an uncapped list floods the grid row.
/// </summary>
public sealed class SubsystemRowManualSuiteTests
{
    private static SubsystemRow Row(params RegressionSuiteRef[] manual) => new(
        Component: "AAMxCore",
        Subsystem: "Core",
        Category: RegressionCategoryKind.Runtime,
        CategoryConfidence: RegressionEvidenceKind.Declared,
        FilesModified: [],
        TotalFilesModified: 0,
        Changes: [],
        RiskTier: "succeeded",
        AutomatedSuites: [],
        ManualSuites: manual,
        EstimatedMinutes: 0,
        IsEstimate: true);

    private static RegressionSuiteRef Suite(int id, string? title, bool linked = true) =>
        new(id.ToString(), linked, linked ? $"https://dev.azure.test/_workitems/edit/{id}" : null,
            RegressionEvidenceKind.Observed, title);

    private static SubsystemRowViewModel Vm(params RegressionSuiteRef[] manual) => new(Row(manual));

    private static RegressionSuiteRef[] Many(int count) =>
        Enumerable.Range(1, count).Select(i => Suite(4000 + i, $"Verify scenario {i}")).ToArray();

    [Fact]
    public void ManualSuiteLinks_Should_PreferTitle_Over_Id()
    {
        ManualSuiteLink link = Assert.Single(Vm(Suite(418823, "Galaxy node failover")).ManualSuiteLinks);

        Assert.Equal("Galaxy node failover", link.Display);
        Assert.Equal("418823", link.SuiteId);
    }

    [Fact]
    public void ManualSuiteLinks_Should_FallBackToId_When_TitleMissing()
    {
        ManualSuiteLink link = Assert.Single(Vm(Suite(418823, null)).ManualSuiteLinks);

        Assert.Equal("TC 418823", link.Display);
        Assert.Contains("no title resolved", link.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualSuiteLinks_Should_CarryIdAndTitleInTooltip()
    {
        ManualSuiteLink link = Assert.Single(Vm(Suite(418823, "Galaxy node failover")).ManualSuiteLinks);

        Assert.Contains("418823", link.Tooltip, StringComparison.Ordinal);
        Assert.Contains("Galaxy node failover", link.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualSuiteLinks_Should_NotBeLinked_When_UrlMissing()
    {
        // A hyperlink that cannot navigate is worse than plain text.
        ManualSuiteLink link = Assert.Single(Vm(Suite(418823, "Some case", linked: false)).ManualSuiteLinks);

        Assert.False(link.IsLinked);
    }

    [Fact]
    public void VisibleManualSuites_Should_CapAtEleven()
    {
        SubsystemRowViewModel vm = Vm(Many(30));

        Assert.Equal(11, vm.VisibleManualSuites.Count);
        Assert.Equal(19, vm.OverflowManualSuites.Count);
        Assert.True(vm.HasOverflowManualSuites);
        Assert.Equal("+19 more", vm.OverflowManualSuiteLabel);
    }

    [Fact]
    public void VisibleManualSuites_Should_ShowAll_When_AtOrUnderCap()
    {
        SubsystemRowViewModel vm = Vm(Many(11));

        Assert.Equal(11, vm.VisibleManualSuites.Count);
        Assert.Empty(vm.OverflowManualSuites);
        Assert.False(vm.HasOverflowManualSuites);
    }

    [Fact]
    public void VisibleAndOverflow_Should_PartitionWithoutLoss()
    {
        SubsystemRowViewModel vm = Vm(Many(30));

        string[] combined = vm.VisibleManualSuites.Concat(vm.OverflowManualSuites)
            .Select(l => l.SuiteId).ToArray();

        Assert.Equal(vm.ManualSuiteLinks.Select(l => l.SuiteId), combined);
    }

    [Fact]
    public void HasManualSuites_Should_BeFalse_When_None()
    {
        SubsystemRowViewModel vm = Vm();

        Assert.False(vm.HasManualSuites);
        Assert.False(vm.HasOverflowManualSuites);
        Assert.Empty(vm.ManualSuitesClipboardText);
    }

    [Fact]
    public void ManualSuitesClipboardText_Should_IncludeOverflowRows()
    {
        SubsystemRowViewModel vm = Vm(Many(30));

        string[] lines = vm.ManualSuitesClipboardText.Split(Environment.NewLine);

        Assert.Equal(30, lines.Length);
        Assert.StartsWith("4001\tVerify scenario 1\t", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("4030\tVerify scenario 30\t", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ManualSuitesClipboardText_Should_BeTabSeparated_ForSpreadsheetPaste()
    {
        string line = Vm(Suite(418823, "Galaxy node failover")).ManualSuitesClipboardText;

        Assert.Equal(
            "418823\tGalaxy node failover\thttps://dev.azure.test/_workitems/edit/418823",
            line);
    }
}
