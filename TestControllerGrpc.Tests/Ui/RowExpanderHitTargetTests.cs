using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// The Code Churn chevron is the only way to reopen a row once its details are hidden, and it sits in a
/// 26px column beside rows that wrap to several lines tall. Top-aligning the BUTTON (rather than the glyph
/// inside its template) collapsed the clickable area to the height of one character, which read to users as
/// "expand is broken" rather than "you missed a 15px target".
///
/// Pure file analysis, like the other Ui guards.
/// </summary>
public sealed class RowExpanderHitTargetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string RegressionWindow() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "Regression", "RegressionWindow.xaml"));

    private static string ExpanderMarkup()
    {
        var xaml = RegressionWindow();
        var match = Regex.Match(
            xaml,
            "<ToggleButton IsChecked=\"\\{Binding IsExpanded.*?</ToggleButton>",
            RegexOptions.Singleline);

        Assert.True(match.Success, "The row-expander ToggleButton was not found - did its IsExpanded binding change?");
        return match.Value;
    }

    [Fact]
    public void RowExpander_Should_FillTheCell_When_TheRowIsTall()
    {
        string markup = ExpanderMarkup();
        string opening = markup[..markup.IndexOf('>')];

        Assert.False(
            Regex.IsMatch(opening, "VerticalAlignment\\s*=\\s*\"(Top|Center|Bottom)\""),
            "The expander ToggleButton pins VerticalAlignment, so it sizes to its glyph instead of stretching " +
            "to fill the cell. Align the TextBlock inside its ControlTemplate instead - the hit target is the button.");
    }

    [Fact]
    public void RowExpander_Should_KeepTheGlyphTopAligned_When_TheRowIsTall()
    {
        Assert.Matches("<TextBlock x:Name=\"chev\"[^>]*VerticalAlignment=\"Top\"", ExpanderMarkup());
    }

    /// <summary>A transparent background is what makes the stretched area hit-testable at all.</summary>
    [Fact]
    public void RowExpander_Should_KeepATransparentBackdrop_When_TemplateChanges()
    {
        Assert.Contains("<Border Background=\"Transparent\">", ExpanderMarkup(), StringComparison.Ordinal);
    }
}
