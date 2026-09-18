using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-9 (WCAG 2.1 AA, 1.4.3). Contrast is the one accessibility property that can be verified without
/// rendering, and the one most easily destroyed by a well-meaning palette tweak. Several of these pairs
/// were genuinely unreadable before this guard existed - light-theme amber on white measured 2.53:1.
///
/// Alpha-bearing tokens are composited over their real backdrop first: judging "#22FFFFFF" raw reports
/// a contrast that never appears on screen.
/// </summary>
public sealed class ThemeContrastTests
{
    private const double AaNormalText = 4.5;

    private static readonly string[] Themes = ["LightTheme", "DarkTheme", "HighContrastTheme"];

    /// <summary>Foreground token -> the surfaces this app actually paints it on.</summary>
    private static readonly (string Fg, string[] Bgs)[] Pairs =
    [
        ("TextP", ["WindowBg", "BgPanel", "BgCard", "BgSurface", "FieldBg", "RibbonBg"]),
        ("TextS", ["WindowBg", "BgPanel", "BgCard", "RibbonBg"]),
        ("TextTertiaryBrush", ["BgPanel", "BgCard"]),
        ("Accent", ["BgPanel", "BgCard"]),
        ("AccGreen", ["BgPanel", "BgCard"]),
        ("AccRed", ["BgPanel", "BgCard"]),
        ("AccYellow", ["BgPanel", "BgCard"]),
        ("AccMauve", ["BgPanel", "BgCard"]),
        ("AccBlue", ["BgPanel", "BgCard"]),
        ("StatusRunningFg", ["StatusRunningBg"]),
        ("StatusSuccessFg", ["StatusSuccessBg"]),
        ("StatusFailedFg", ["StatusFailedBg"]),
        ("StatusWarningFg", ["StatusWarningBg"]),
        ("StatusRebootFg", ["StatusRebootBg"]),
        ("StatusMaintenanceFg", ["StatusMaintenanceBg"]),
        ("StatusDrainingFg", ["StatusDrainingBg"]),
        ("TreeBadgeFg", ["AccMauve", "Accent", "AccRed"]),
    ];

    private readonly record struct Rgba(double A, double R, double G, double B);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Dictionary<string, Rgba> ReadTokens(string theme)
    {
        var path = Path.Combine(RepoRoot(), "TestControllerGrpc", "Themes", $"{theme}.xaml");
        var tokens = new Dictionary<string, Rgba>();

        foreach (Match m in Regex.Matches(File.ReadAllText(path), "x:Key=\"([^\"]+)\"\\s+Color=\"#([0-9A-Fa-f]{6,8})\""))
            tokens[m.Groups[1].Value] = Parse(m.Groups[2].Value);

        return tokens;
    }

    private static Rgba Parse(string hex)
    {
        if (hex.Length == 6) hex = "FF" + hex;
        static double Byte(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return new Rgba(Byte(hex[..2]) / 255.0, Byte(hex.Substring(2, 2)), Byte(hex.Substring(4, 2)), Byte(hex.Substring(6, 2)));
    }

    private static Rgba Over(Rgba fg, Rgba bg) => new(
        1.0,
        fg.R * fg.A + bg.R * (1 - fg.A),
        fg.G * fg.A + bg.G * (1 - fg.A),
        fg.B * fg.A + bg.B * (1 - fg.A));

    private static double Luminance(Rgba c)
    {
        static double Channel(double v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Rgba fg, Rgba bg)
    {
        var l1 = Luminance(Over(fg, bg));
        var l2 = Luminance(bg);
        if (l2 > l1) (l1, l2) = (l2, l1);

        return (l1 + 0.05) / (l2 + 0.05);
    }

    [Theory]
    [InlineData("LightTheme")]
    [InlineData("DarkTheme")]
    [InlineData("HighContrastTheme")]
    public void EveryTextPair_Should_MeetAa_When_ThemeColoursChange(string theme)
    {
        var tokens = ReadTokens(theme);
        var canvas = tokens["WindowBg"];
        var failures = new List<string>();

        foreach (var (fgName, bgNames) in Pairs)
        {
            if (!tokens.TryGetValue(fgName, out var fg)) continue;

            foreach (var bgName in bgNames)
            {
                if (!tokens.TryGetValue(bgName, out var rawBg)) continue;

                // Surfaces are themselves often translucent, so resolve them against the window canvas.
                var ratio = Contrast(fg, Over(rawBg, canvas));
                if (ratio < AaNormalText)
                    failures.Add($"{fgName} on {bgName} = {ratio:F2}:1");
            }
        }

        Assert.True(failures.Count == 0,
            $"{theme} has {failures.Count} pair(s) below AA {AaNormalText}:1:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", failures));
    }

    /// <summary>Guards the maths itself - a contrast function that always returns a big number proves nothing.</summary>
    [Fact]
    public void Contrast_Should_MatchKnownValues_When_ComputedForReferenceColours()
    {
        var white = Parse("FFFFFFFF");
        var black = Parse("FF000000");

        Assert.Equal(21.0, Contrast(black, white), 1);
        Assert.Equal(1.0, Contrast(white, white), 1);
        // #767676 on white is the canonical WCAG AA boundary for normal text.
        Assert.Equal(4.54, Contrast(Parse("FF767676"), white), 2);
    }
}
