using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-9 (WCAG 2.4.7 Focus Visible). WPF's stock focus visual is a 1px dotted rectangle that all but
/// disappears on the dark and high-contrast themes, so a keyboard user cannot tell where they are.
/// The app overrides the SYSTEM focus key, which reaches controls carrying an explicit Style too -
/// an implicit style would not.
/// </summary>
public sealed class FocusVisualTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> ViewFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void App_Should_OverrideTheSystemFocusVisual_When_KeyboardUsersNeedToSeeFocus()
    {
        var path = Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "Styles", "ControlStyles.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("SystemParameters.FocusVisualStyleKey", xaml);

        // A ring drawn in a theme brush is the whole point: a hardcoded colour would vanish in one theme.
        var style = Regex.Match(
            xaml,
            "FocusVisualStyleKey.*?</Style>",
            RegexOptions.Singleline);

        Assert.True(style.Success, "Focus visual style block not found.");
        Assert.Contains("DynamicResource", style.Value);
    }

    [Fact]
    public void Views_Should_NotSuppressFocusVisuals_When_StylingControls()
    {
        var offenders = ViewFiles()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "FocusVisualStyle=\"\\{x:Null\\}\""))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files remove the focus ring, leaving keyboard users with no position indicator: "
            + string.Join(", ", offenders));
    }
}
