using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-4/G-6 elevation. The UI kit gives cards a soft shadow and dialogs a stronger one - that layering is
/// what makes the light theme read as depth rather than flat white. High contrast must have NO shadow:
/// soft depth cues defeat the point, and the hard borders carry separation instead.
/// </summary>
public sealed class ElevationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Theme(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "TestControllerGrpc", "Themes", $"{name}.xaml"));

    private static double OpacityOf(string xaml, string key)
    {
        var m = Regex.Match(xaml, $"x:Key=\"{key}\"[^/]*?Opacity=\"([\\d.]+)\"", RegexOptions.Singleline);
        Assert.True(m.Success, $"{key} not found or has no Opacity.");
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("LightTheme")]
    [InlineData("DarkTheme")]
    public void Themes_Should_GiveCardsAndDialogsElevation_When_NotHighContrast(string theme)
    {
        var xaml = Theme(theme);

        Assert.True(OpacityOf(xaml, "ShadowCard") > 0, $"{theme} ShadowCard is invisible.");
        // A dialog must read as floating ABOVE the cards, or the layering conveys nothing.
        Assert.True(OpacityOf(xaml, "ShadowDialog") > OpacityOf(xaml, "ShadowCard"),
            $"{theme} dialog shadow must be stronger than the card shadow.");
    }

    [Fact]
    public void HighContrast_Should_HaveNoShadows_When_DepthWouldObscureBorders()
    {
        var xaml = Theme("HighContrastTheme");

        Assert.Equal(0, OpacityOf(xaml, "ShadowCard"));
        Assert.Equal(0, OpacityOf(xaml, "ShadowDialog"));
    }

    /// <summary>
    /// Elevation belongs to the shared card styles; applied per-view it drifts and cannot be retuned
    /// in one place, which is the whole reason for a token layer.
    /// </summary>
    [Fact]
    public void SharedCardStyles_Should_UseTheElevationToken_When_Defined()
    {
        var controls = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "Styles", "ControlStyles.xaml"));

        foreach (var style in new[] { "CardBorderStyle", "PanelBorder", "KpiCardStyle" })
        {
            var block = Regex.Match(controls, $"<Style x:Key=\"{style}\".*?</Style>", RegexOptions.Singleline);
            Assert.True(block.Success, $"{style} not found.");
            Assert.Contains("ShadowCard", block.Value);
        }
    }

    private static IEnumerable<string> NonThemeXaml() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// An inline DropShadowEffect cannot be switched off by a theme, so it keeps casting a shadow in
    /// high contrast - where depth cues actively work against the hard-border design.
    /// </summary>
    [Fact]
    public void Views_Should_NotDeclareShadowsInline_When_ThemeTokensExist()
    {
        var offenders = NonThemeXaml()
            .Where(p => File.ReadAllText(p).Contains("<DropShadowEffect"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Inline shadows survive in: {string.Join(", ", offenders)}. Use ShadowCard/ShadowDialog.");
    }

    [Fact]
    public void DialogElevation_Should_BeApplied_When_TokenIsDefined()
    {
        var users = NonThemeXaml().Count(p => File.ReadAllText(p).Contains("Resource ShadowDialog}"));

        Assert.True(users >= 2,
            $"ShadowDialog is defined but referenced by only {users} file(s); dialogs and popups should use it.");
    }
}
