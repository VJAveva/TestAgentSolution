using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-7 ratchet. Chrome grows back one padding at a time: nobody ever adds 40px deliberately, but a
/// sequence of "just a bit more room" edits does. These pin the measurements that were reclaimed so a
/// regression is a failing build rather than a slow creep.
///
/// Pure file analysis, like the other Ui guards.
/// </summary>
public sealed class CompactChromeTests
{
    private const int RibbonButtonHeightBudget = 44;
    private const int RibbonIconBudgetPx = 16;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string MainWindow() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "MainWindow.xaml"));

    /// <summary>The ribbon button box is the single largest contributor to chrome height.</summary>
    [Fact]
    public void RibbonButton_Should_StayWithinHeightBudget_When_RibbonChanges()
    {
        var xaml = MainWindow();
        var style = Regex.Match(xaml, "<Style x:Key=\"RibbonBtn\" TargetType=\"Button\">(.*?)</Style>", RegexOptions.Singleline);
        Assert.True(style.Success, "RibbonBtn style not found - did it move or get renamed?");

        var height = Regex.Match(style.Groups[1].Value, "<Setter Property=\"Height\" Value=\"(\\d+)\" />");
        Assert.True(height.Success, "RibbonBtn no longer sets an explicit Height; the budget cannot be enforced.");

        var px = int.Parse(height.Groups[1].Value);
        Assert.True(px <= RibbonButtonHeightBudget,
            $"Ribbon button height grew to {px}px (budget {RibbonButtonHeightBudget}px). "
            + "Every ribbon row pays this cost twice over once padding and the caption are added.");
    }

    /// <summary>
    /// The glyph drives the button's minimum height, so a bigger icon silently re-inflates the ribbon
    /// even when the height setter is untouched.
    /// </summary>
    [Fact]
    public void RibbonIcon_Should_StayWithinBudget_When_TypographyChanges()
    {
        var xaml = MainWindow();
        var style = Regex.Match(
            xaml,
            "<Style x:Key=\"RibbonIconText\".*?<Setter Property=\"FontSize\" Value=\"\\{StaticResource FontSize(\\d+)\\}\" />",
            RegexOptions.Singleline);
        Assert.True(style.Success, "RibbonIconText no longer references a FontSize token.");

        // Token names track their value here (FontSize16 = 16), unlike the nudged small end of the scale.
        var token = int.Parse(style.Groups[1].Value);
        Assert.True(token <= RibbonIconBudgetPx,
            $"Ribbon icon token rose to FontSize{token} (budget FontSize{RibbonIconBudgetPx}).");
    }

    /// <summary>
    /// The agent pane and execution log headers are the same visual element; if one is padded back out
    /// they stop lining up, which reads as a layout bug rather than a spacing choice.
    /// </summary>
    [Fact]
    public void PanelHeaders_Should_StayThin_And_Matched_When_PanesChange()
    {
        var xaml = MainWindow();

        // Both headers come from PanelHeaderBorder now; the compact padding is the per-instance override.
        var thin = Regex.Matches(xaml, "Style=\"\\{(?:Static|Dynamic)Resource PanelHeaderBorder\\}\" Padding=\"8,2\"").Count;
        Assert.True(thin == 2,
            $"Expected 2 thin panel headers (agent pane + execution log), found {thin}. "
            + "They must stay identical or they visibly mismatch.");
    }
}
