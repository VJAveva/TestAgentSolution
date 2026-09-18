using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Wave 0 of the UI token work. A WPF <c>DynamicResource</c> that resolves to nothing fails SILENTLY -
/// the control simply renders with no brush. No exception, no failing test, and it only shows up when
/// somebody switches to the affected theme. These tests turn that class of bug into a build failure.
///
/// They are pure file analysis: no STA thread, no WPF instantiation, so they cannot flake.
/// </summary>
public sealed class ThemeResourceParityTests
{
    private static readonly string[] ThemeFiles =
    [
        "DarkTheme.xaml",
        "LightTheme.xaml",
        "HighContrastTheme.xaml",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ThemeDirectory() => Path.Combine(RepoRoot(), "TestControllerGrpc", "Themes");

    private static IReadOnlyList<string> KeysIn(string themeFile)
    {
        var path = Path.Combine(ThemeDirectory(), themeFile);
        Assert.True(File.Exists(path), $"Theme file not found: {path}");

        return Regex.Matches(File.ReadAllText(path), "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    /// <summary>Every .xaml in the WPF app except the theme dictionaries themselves.</summary>
    private static IReadOnlyList<string> ViewXamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}"))
            .ToList();

    [Fact]
    public void AllThemes_Should_DefineTheSameKeys_When_Compared()
    {
        var byTheme = ThemeFiles.ToDictionary(f => f, f => KeysIn(f).ToHashSet(StringComparer.Ordinal));
        var union = byTheme.Values.SelectMany(k => k).ToHashSet(StringComparer.Ordinal);

        var gaps = new List<string>();
        foreach (var (theme, keys) in byTheme)
            foreach (var missing in union.Except(keys).OrderBy(k => k, StringComparer.Ordinal))
                gaps.Add($"{theme} is missing '{missing}'");

        // A key defined in one theme but not another renders as no brush at all in the themes that lack it.
        Assert.True(gaps.Count == 0,
            $"Theme dictionaries are out of sync ({gaps.Count} gap(s)):{Environment.NewLine}{string.Join(Environment.NewLine, gaps)}");
    }

    [Fact]
    public void EveryThemeOwnedKeyUsedInAView_Should_ResolveInEveryTheme()
    {
        var byTheme = ThemeFiles.ToDictionary(f => f, f => KeysIn(f).ToHashSet(StringComparer.Ordinal));
        var themeOwned = byTheme.Values.SelectMany(k => k).ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var file in ViewXamlFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"DynamicResource\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}"))
            {
                var key = m.Groups[1].Value;
                if (!themeOwned.Contains(key)) continue;   // local or framework resource, not ours to police

                foreach (var (theme, keys) in byTheme)
                    if (!keys.Contains(key))
                        problems.Add($"{Path.GetFileName(file)} uses '{key}', absent from {theme}");
            }
        }

        Assert.True(problems.Count == 0,
            $"Views reference theme brushes that do not exist in every theme:{Environment.NewLine}" +
            string.Join(Environment.NewLine, problems.Distinct().OrderBy(p => p, StringComparer.Ordinal)));
    }

    [Fact]
    public void ThemeDictionaries_Should_NotDefineDuplicateKeys()
    {
        var duplicates = new List<string>();
        foreach (var theme in ThemeFiles)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in KeysIn(theme))
                if (!seen.Add(key))
                    duplicates.Add($"{theme} defines '{key}' more than once");
        }

        // A later duplicate silently wins, so the value you read in the file is not the value that renders.
        Assert.True(duplicates.Count == 0, string.Join(Environment.NewLine, duplicates));
    }
}
