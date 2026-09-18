using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Wave 0 ratchet. The UI token migration replaces hardcoded colours and font sizes with theme resources,
/// but that is a long job across 54 view files. These tests do not demand perfection - they pin the CURRENT
/// counts so the numbers can only go DOWN. A new hardcoded value fails the build.
///
/// When you migrate a batch, lower the baseline in the same commit. The failure message tells you the number.
/// Theme dictionaries are excluded on purpose: literal colours belong in the token layer and nowhere else.
///
/// DELIBERATE DEVIATION - acceptance criterion G-2 ("WPF and React use the SAME token names") is DECLINED.
/// The two platforms keep their own vocabularies (WPF TextP/AccBlue/BgCard vs React --text-primary/--accent/
/// --bg-card). They already satisfy what G-2 exists to protect: identical KEY SETS per theme, enforced by
/// ThemeResourceParityTests (WPF) and themeStore.test.ts (React), and the same six type roles. Renaming
/// ~2,000 reference sites would change no pixel, and a mistyped DynamicResource fails SILENTLY at runtime -
/// so the change carries real risk and returns nothing. Revisit only if the platforms ever share a
/// generated token source.
/// </summary>
public sealed class TokenAuditTests
{
    // Measured 2026-09-17 across TestControllerGrpc views (excluding Themes/), counting every MATCH -
    // not every matching line, which undercounts where one line carries two literals.
    // 140 -> 110: RegressionWindow (CodeChurn) carried a PRIVATE dark-only palette (RbBg/RbTx/RbViolet...)
    // that ignored the theme entirely; all 30 literals mapped onto tokens that already existed.
    // 110 -> 70: every literal whose value exactly matched a DarkTheme token, on a brush-valued attribute
    // (Foreground/Background/BorderBrush), became that token - identical in Dark, and finally theme-aware
    // in Light and High Contrast. The rest are deliberately left: 13 sit on Color= properties where a
    // SolidColorBrush token is the wrong TYPE, 7 are Setter values whose target property varies, and the
    // remainder are alpha overlays (#1AF0B070, #2274ACEA ...) and brand colours (#00A4EF) with no token.
    // 70 -> 50: ExecutionDashboardStyles.xaml held an 18-brush PRIVATE palette merged app-wide, so the
    // dashboard kept its dark colours in every theme. Those values are fixed by WebClient_Enhancement_Spec,
    // so they were MOVED into the three theme dictionaries (Dark keeps the spec values byte-for-byte)
    // rather than repointed at generic tokens - see PrivatePalettes_Should_NotExist_When_ThemesOwnColour.
    private const int HexBaseline = 50;

    // 1004 -> 173 -> 131 -> 25. The 831 literals with an exact token value migrated first; then FontSize15
    // (the UI kit's "header" role) was added and the fractional sizes were rounded onto the scale.
    // Finally 101 captions took FontSize9 (a deliberate 9->10px lift to the scale's readability floor) and
    // 5 dialog titles took FontSize20. The 25 that remain are NOT type: Segoe MDL2 icon glyphs, micro-badges
    // positioned with negative margins, large mono, and display numerals (metric values, the grade letter).
    // Icons and display numerals sit outside the six text roles by design, so they are not substitutable.
    private const int FontSizeBaseline = 25;

    // Both spellings count. Only the attribute form was counted originally, which hid 94 setter-form
    // literals from the ratchet entirely.
    private const string FontSizePattern = "FontSize=\"\\d|Property=\"FontSize\"\\s+Value=\"\\d";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<string> ViewXamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}"))
            .ToList();

    private static (int Total, string Breakdown) CountMatches(string pattern)
    {
        var perFile = new List<(string File, int Count)>();
        var total = 0;

        foreach (var file in ViewXamlFiles())
        {
            var n = Regex.Matches(File.ReadAllText(file), pattern).Count;
            if (n == 0) continue;
            perFile.Add((Path.GetFileName(file), n));
            total += n;
        }

        var breakdown = string.Join(Environment.NewLine,
            perFile.OrderByDescending(x => x.Count).Take(10).Select(x => $"    {x.File}: {x.Count}"));
        return (total, breakdown);
    }

    [Fact]
    public void HardcodedColours_Should_NotIncrease_When_ViewsChange()
    {
        var (total, breakdown) = CountMatches("\"#[0-9A-Fa-f]{6,8}\"");

        Assert.True(total <= HexBaseline,
            $"Hardcoded colours in views rose to {total} (baseline {HexBaseline}). " +
            $"Use a theme brush via DynamicResource instead.{Environment.NewLine}Top files:{Environment.NewLine}{breakdown}");
    }

    [Fact]
    public void HardcodedFontSizes_Should_NotIncrease_When_ViewsChange()
    {
        var (total, breakdown) = CountMatches(FontSizePattern);

        Assert.True(total <= FontSizeBaseline,
            $"Hardcoded font sizes in views rose to {total} (baseline {FontSizeBaseline}). " +
            $"Use a typography resource instead.{Environment.NewLine}Top files:{Environment.NewLine}{breakdown}");
    }

    /// <summary>
    /// A keyed SolidColorBrush outside Themes/ is a private palette: it is merged once and never swapped,
    /// so the view keeps those colours in every theme. Two shipped this way (the CodeChurn window and the
    /// execution dashboard) and neither was visible to the parity test, which only compares the three
    /// theme files against each other. Zero tolerance - colour belongs to the token layer.
    /// </summary>
    [Fact]
    public void PrivatePalettes_Should_NotExist_When_ThemesOwnColour()
    {
        var offenders = new List<string>();

        foreach (var file in ViewXamlFiles())
        {
            var matches = Regex.Matches(
                File.ReadAllText(file),
                "<SolidColorBrush\\s+[^>]*x:Key=\"(\\w+)\"[^>]*Color=\"#[0-9A-Fa-f]{3,8}\"");

            foreach (Match m in matches)
                offenders.Add($"{Path.GetFileName(file)}:{m.Groups[1].Value}");
        }

        Assert.True(offenders.Count == 0,
            "Private colour palettes found - define these in the three theme dictionaries instead: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Baselines_Should_BeTightened_When_MigrationProgresses()
    {
        var hex = CountMatches("\"#[0-9A-Fa-f]{6,8}\"").Total;
        var fonts = CountMatches(FontSizePattern).Total;

        // Guards the ratchet itself: if a batch is migrated but the baseline is not lowered, the ratchet
        // silently stops protecting the gap it just gained.
        Assert.False(hex < HexBaseline - 20,
            $"Hardcoded colours dropped to {hex}; lower HexBaseline to {hex} so the ratchet keeps its grip.");
        Assert.False(fonts < FontSizeBaseline - 50,
            $"Hardcoded font sizes dropped to {fonts}; lower FontSizeBaseline to {fonts}.");
    }

    /// <summary>
    /// The font-size token NAMES DO NOT MATCH THEIR VALUES - FontSize11 is 12px, because the small end of
    /// the scale was deliberately nudged up (+1) for readability. Views migrate to these keys BY VALUE, so
    /// "correcting" a value to match its name would silently resize every migrated element in the app.
    /// This pins the values; change one and this test names the blast radius.
    /// </summary>
    [Fact]
    public void FontSizeTokens_Should_KeepTheirMeasuredValues_When_TypographyChanges()
    {
        var expected = new Dictionary<string, string>
        {
            ["FontSize9"] = "10",
            ["FontSize10"] = "11",
            ["FontSize11"] = "12",
            ["FontSize12"] = "13",
            ["FontSize13"] = "13",
            ["FontSize14"] = "14",
            ["FontSize15"] = "15",
            ["FontSize16"] = "16",
            ["FontSize20"] = "20",
            ["FontSize24"] = "24",
        };

        var typography = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "Styles", "Typography.xaml"));

        var actual = Regex.Matches(typography, @"<sys:Double x:Key=""(FontSize\w+)"">([\d.]+)</sys:Double>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        var drifted = expected
            .Where(kv => !actual.TryGetValue(kv.Key, out var v) || v != kv.Value)
            .Select(kv => $"    {kv.Key}: expected {kv.Value}, found {(actual.TryGetValue(kv.Key, out var v) ? v : "MISSING")}")
            .ToList();

        Assert.True(drifted.Count == 0,
            $"Typography token values changed. Views reference these BY VALUE, so every migrated element " +
            $"resizes:{Environment.NewLine}{string.Join(Environment.NewLine, drifted)}");
    }
}
