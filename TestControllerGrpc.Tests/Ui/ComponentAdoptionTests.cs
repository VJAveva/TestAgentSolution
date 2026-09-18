using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-4. The mockup's rule is that views are ASSEMBLED from the shared component library, not that they
/// merely end up looking similar. The app violated this wholesale: the shared card/panel styles had zero
/// uses while dozens of cards were hand-rolled inline with divergent radii (4, 6 and 8 all in use).
/// These guards ratchet the inline count down and pin the shared card to the mockup's measurements.
/// </summary>
public sealed class ComponentAdoptionTests
{
    /// <summary>Hand-rolled card borders still awaiting migration. May only ever decrease.</summary>
    private const int InlineCardBaseline = 25;

    private static readonly Regex BorderTag = new(@"<Border\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex SurfaceBg =
        new(@"Background=""\{(?:Dynamic|Static)Resource (?:CardBg|BgCard|BgPanel)\}""", RegexOptions.Compiled);
    private static readonly Regex LiteralRadius = new(@"CornerRadius=""\d", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>View markup only - the theme and style dictionaries are where literals belong.</summary>
    private static IEnumerable<string> ViewFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}")
                          && !p.Contains(Path.Combine("Views", "Styles")));

    private static int InlineCardsIn(string xaml) =>
        BorderTag.Matches(xaml).Count(m => SurfaceBg.IsMatch(m.Value) && LiteralRadius.IsMatch(m.Value));

    private static string Styles() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "Styles", "ControlStyles.xaml"));

    private static string CardStyleBlock()
    {
        var m = Regex.Match(Styles(), "<Style x:Key=\"CardBorderStyle\".*?</Style>", RegexOptions.Singleline);
        Assert.True(m.Success, "CardBorderStyle is missing from ControlStyles.xaml.");
        return m.Value;
    }

    [Fact]
    public void InlineCards_Should_NotIncrease_When_ViewsAreEdited()
    {
        var worst = new List<string>();
        var total = 0;

        foreach (var path in ViewFiles())
        {
            var count = InlineCardsIn(File.ReadAllText(path));
            if (count > 0)
            {
                total += count;
                worst.Add($"{Path.GetFileName(path)}: {count}");
            }
        }

        Assert.True(total <= InlineCardBaseline,
            $"Hand-rolled cards rose to {total} (baseline {InlineCardBaseline}). " +
            $"Use CardBorderStyle instead of an inline Border. Offenders: {string.Join(", ", worst)}");
    }

    [Fact]
    public void CardBorderStyle_Should_MatchMockupCard_When_Measured()
    {
        var style = CardStyleBlock();

        // --surface is #ffffff in light; CardBg matches it, BgCard is --surface-alt. Easy to confuse.
        Assert.Contains("Value=\"{DynamicResource CardBg}\"", style);
        Assert.Contains("Value=\"{DynamicResource RowBorder}\"", style);
        Assert.Contains("Value=\"{DynamicResource RadiusLg}\"", style);
        Assert.Contains("Value=\"{DynamicResource ShadowCard}\"", style);
    }

    [Theory]
    [InlineData("RadiusSm", 6)]
    [InlineData("RadiusMd", 8)]
    [InlineData("RadiusLg", 10)]
    [InlineData("RadiusXl", 12)]
    public void RadiusScale_Should_MirrorMockup_When_TokensAreRead(string key, int expected)
    {
        var tokens = File.ReadAllText(
            Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "Styles", "DesignTokens.xaml"));

        var m = Regex.Match(tokens, $"<CornerRadius x:Key=\"{key}\">([\\d.]+)</CornerRadius>");
        Assert.True(m.Success, $"{key} is missing from DesignTokens.xaml.");
        Assert.Equal(expected, int.Parse(m.Groups[1].Value));
    }

    [Fact]
    public void MonitorView_Should_UseTheSharedCard_When_RenderingTelemetry()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "AgentWorkspace", "MonitorView.xaml"));

        Assert.Equal(0, InlineCardsIn(xaml));
        Assert.True(Regex.Matches(xaml, "(?:Static|Dynamic)Resource CardBorderStyle").Count >= 5,
            "MonitorView's telemetry cards should all come from CardBorderStyle.");
    }
}
