using System.Windows;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Tests for BuildResultsModels: BuildListItem, ResultsTreeNode,
/// ResultsFlatNode, UseCaseTrendViewModel, FlakyClassificationHelper.
/// </summary>
public class BuildResultsModelsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // BuildListItem
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildListItem_ToString_Should_FormatCorrectly()
    {
        var item = new BuildListItem
        {
            BuildNumber = "Build-42",
            ModifiedDate = new DateTime(2025, 1, 15, 14, 30, 0)
        };
        Assert.Equal("Build-42  (2025-01-15 14:30)", item.ToString());
    }

    // ═══════════════════════════════════════════════════════════════════
    // ResultsTreeNode
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TreeNode_PassRateFormatted_Should_FormatPercentage_When_HasValue()
    {
        var node = new ResultsTreeNode { PassRate = 87.654 };
        Assert.Equal("87.7%", node.PassRateFormatted);
    }

    [Fact]
    public void TreeNode_PassRateFormatted_Should_ReturnEmpty_When_Null()
    {
        var node = new ResultsTreeNode();
        Assert.Equal("", node.PassRateFormatted);
    }

    [Fact]
    public void TreeNode_HasChildren_Should_ReturnTrue_When_ChildrenExist()
    {
        var node = new ResultsTreeNode();
        node.Children.Add(new ResultsTreeNode { Name = "Child" });
        Assert.True(node.HasChildren);
    }

    [Fact]
    public void TreeNode_HasChildren_Should_ReturnFalse_When_Empty()
    {
        var node = new ResultsTreeNode();
        Assert.False(node.HasChildren);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ResultsFlatNode
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FlatNode_IndentMargin_Should_ScaleByLevel()
    {
        var node = new ResultsFlatNode { IndentLevel = 3 };
        Assert.Equal(new Thickness(60, 0, 0, 0), node.IndentMargin);
    }

    [Fact]
    public void FlatNode_IndentMargin_Should_BeZero_When_LevelZero()
    {
        var node = new ResultsFlatNode { IndentLevel = 0 };
        Assert.Equal(new Thickness(0, 0, 0, 0), node.IndentMargin);
    }

    [Fact]
    public void FlatNode_ExpandIcon_Should_BeDownArrow_When_Expanded()
    {
        var node = new ResultsFlatNode { IsExpanded = true };
        Assert.Equal("\u25BC", node.ExpandIcon);
    }

    [Fact]
    public void FlatNode_ExpandIcon_Should_BeRightArrow_When_Collapsed()
    {
        var node = new ResultsFlatNode { IsExpanded = false };
        Assert.Equal("\u25B6", node.ExpandIcon);
    }

    [Theory]
    [InlineData("AllBuilds", true)]
    [InlineData("Build", true)]
    [InlineData("UseCase", true)]
    [InlineData("TestResult", false)]
    public void FlatNode_CanExpand_Should_DependOnNodeLevel(string level, bool expected)
    {
        var node = new ResultsFlatNode { NodeLevel = level };
        Assert.Equal(expected, node.CanExpand);
    }

    [Theory]
    [InlineData("Build", true)]
    [InlineData("AllBuilds", true)]
    [InlineData("UseCase", false)]
    [InlineData("TestResult", false)]
    public void FlatNode_ShowExportButtons_Should_DependOnNodeLevel(
        string level, bool expected)
    {
        var node = new ResultsFlatNode { NodeLevel = level };
        Assert.Equal(expected, node.ShowExportButtons);
    }

    [Theory]
    [InlineData("Passed", "\u2713")]
    [InlineData("Failed", "\u2717")]
    [InlineData("Timeout", "\u23F1")]
    [InlineData("Other", "\u25CB")]
    public void FlatNode_OutcomeIcon_Should_MapCorrectly(
        string outcome, string expected)
    {
        var node = new ResultsFlatNode { Outcome = outcome };
        Assert.Equal(expected, node.OutcomeIcon);
    }

    [Fact]
    public void FlatNode_PassRateFormatted_Should_FormatCorrectly()
    {
        var node = new ResultsFlatNode { PassRate = 99.99 };
        Assert.Equal("100.0%", node.PassRateFormatted);  // F1 format rounds
    }

    // ═══════════════════════════════════════════════════════════════════
    // UseCaseTrendViewModel
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Improving", "\u25B2", "#10B981")]
    [InlineData("Declining", "\u25BC", "#EF4444")]
    [InlineData("Stable", "\u2014", "#94A3B8")]
    public void Trend_Should_MapToCorrectIconAndColor(
        string trend, string expectedIcon, string expectedColor)
    {
        var vm = new UseCaseTrendViewModel { Trend = trend };
        Assert.Equal(expectedIcon, vm.TrendIcon);
        Assert.Equal(expectedColor, vm.TrendColor);
    }

    [Fact]
    public void LatestPassRateFormatted_Should_FormatCorrectly()
    {
        var vm = new UseCaseTrendViewModel { LatestPassRate = 85.7 };
        Assert.Equal("85.7%", vm.LatestPassRateFormatted);
    }

    [Fact]
    public void BuildCount_Should_ReflectEntries()
    {
        var vm = new UseCaseTrendViewModel
        {
            Entries = new List<UseCaseTrendEntry>
            {
                new() { PassRate = 90.0 },
                new() { PassRate = 85.0 },
            }
        };
        Assert.Equal(2, vm.BuildCount);
    }

    [Fact]
    public void SparklineText_Should_ShowLast5Entries()
    {
        var vm = new UseCaseTrendViewModel
        {
            Entries = Enumerable.Range(80, 7).Select(i =>
                new UseCaseTrendEntry { PassRate = i }).ToList()
        };
        // last 5: 82, 83, 84, 85, 86
        Assert.Contains("82", vm.SparklineText);
        Assert.Contains("86", vm.SparklineText);
        Assert.Contains("\u2192", vm.SparklineText); // arrow separator
    }

    [Fact]
    public void SparklineText_Should_BeEmpty_When_NoEntries()
    {
        var vm = new UseCaseTrendViewModel();
        Assert.Equal("", vm.SparklineText);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FlakyClassificationHelper
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Consistent", "#EF4444")]
    [InlineData("Frequent", "#F59E0B")]
    [InlineData("Intermittent", "#60A5FA")]
    [InlineData("Unknown", "#94A3B8")]
    public void FlakyHelper_GetColor_Should_MapCorrectly(
        string classification, string expected)
    {
        Assert.Equal(expected, FlakyClassificationHelper.GetColor(classification));
    }

    [Theory]
    [InlineData("Consistent", "\u2717")]
    [InlineData("Frequent", "\u26A0")]
    [InlineData("Intermittent", "\u223C")]
    [InlineData("Unknown", "\u25CB")]
    public void FlakyHelper_GetIcon_Should_MapCorrectly(
        string classification, string expected)
    {
        Assert.Equal(expected, FlakyClassificationHelper.GetIcon(classification));
    }
}
