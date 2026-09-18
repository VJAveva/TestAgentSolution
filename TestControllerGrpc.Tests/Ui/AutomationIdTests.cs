using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Guards the automation identity layer that the planned FlaUI suite will depend on.
///
/// Why explicit ids rather than relying on x:Name: WPF falls back to the element's Name when no
/// AutomationId is set, but x:Name is a code-behind field that developers rename freely. An AutomationId
/// is a CONTRACT with the UI tests - renaming it must be a deliberate act, not a side effect of a refactor.
///
/// Convention: "View.Element" or "View.Group.Element", PascalCase segments.
///   LockConflict.CloseButton      UserList.RoleFilter.Admin
/// The view prefix makes ids globally unique and makes a FlaUI failure message say where it looked.
///
/// Duplicate ids are the single biggest cause of flaky UI automation - a selector silently matches the
/// wrong control - so uniqueness is enforced hard.
/// </summary>
public sealed class AutomationIdTests
{
    // Raised as each view gains ids. It may never go down: losing an id breaks a FlaUI selector.
    // 2026-09-17: LockConflictDialog piloted the convention with 7; FleetView KPI strip added 6.
    private const int CoverageBaseline = 13;

    private static readonly Regex IdPattern = new(@"^[A-Z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$", RegexOptions.Compiled);

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

    private sealed record AutomationId(string File, int Line, string Value);

    private static IReadOnlyList<AutomationId> AllIds()
    {
        var ids = new List<AutomationId>();
        foreach (var file in ViewXamlFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in Regex.Matches(lines[i], "AutomationProperties\\.AutomationId=\"([^\"]*)\""))
                    ids.Add(new AutomationId(Path.GetFileName(file), i + 1, m.Groups[1].Value));
        }
        return ids;
    }

    [Fact]
    public void AutomationIds_Should_BeGloballyUnique()
    {
        var duplicates = AllIds()
            .GroupBy(id => id.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Duplicate AutomationIds would make a FlaUI selector match the wrong control:{Environment.NewLine}" +
            string.Join(Environment.NewLine, duplicates.Select(g =>
                $"    '{g.Key}' at {string.Join(", ", g.Select(x => $"{x.File}:{x.Line}"))}")));
    }

    [Fact]
    public void AutomationIds_Should_FollowTheNamingConvention()
    {
        var offenders = AllIds()
            .Where(id => !IdPattern.IsMatch(id.Value))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"AutomationIds must be PascalCase 'View.Element' (or 'View.Group.Element'):{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders.Select(o => $"    {o.File}:{o.Line} '{o.Value}'")));
    }

    [Fact]
    public void AutomationIds_Should_NotBeEmpty()
    {
        var blank = AllIds().Where(id => string.IsNullOrWhiteSpace(id.Value)).ToList();

        Assert.True(blank.Count == 0,
            $"Empty AutomationId is worse than none - it hides the x:Name fallback:{Environment.NewLine}" +
            string.Join(Environment.NewLine, blank.Select(b => $"    {b.File}:{b.Line}")));
    }

    [Fact]
    public void AutomationIdCoverage_Should_NotRegress()
    {
        var count = AllIds().Count;

        Assert.True(count >= CoverageBaseline,
            $"AutomationId coverage fell to {count} (baseline {CoverageBaseline}). " +
            "Removing an id breaks whichever FlaUI test selects it.");
    }
}
