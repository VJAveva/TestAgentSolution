using System.IO;
using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Wave 0: resource-reference integrity across the whole WPF app.
///
/// The two ways a restyle breaks a screen:
///   * <c>StaticResource</c> to a key that no longer exists  -> XamlParseException when the view loads (a crash).
///   * <c>DynamicResource</c> to a key that no longer exists -> NOTHING. The control renders with no brush.
///
/// Neither is caught by view-model tests, because no view-model is involved. This walks every .xaml,
/// collects every key DEFINED anywhere, and asserts every key REFERENCED resolves. Pure file analysis,
/// so it runs in milliseconds and cannot flake.
/// </summary>
public sealed class ResourceReferenceIntegrityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<string> AllXaml() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

    /// <summary>Every x:Key defined anywhere in the app - themes, App.xaml, and per-view resource sections -
    /// plus keys injected at runtime from code-behind, e.g. <c>Resources["PassRateConv"] = ...</c>.</summary>
    private static HashSet<string> DefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in AllXaml())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "x:Key=\"([^\"]+)\""))
                keys.Add(m.Groups[1].Value);

        foreach (var file in AllCSharp())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Resources\\[\\s*\"([^\"]+)\"\\s*\\]"))
                keys.Add(m.Groups[1].Value);

        return keys;
    }

    private static IReadOnlyList<string> AllCSharp() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TestControllerGrpc"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

    private sealed record Reference(string File, int Line, string Kind, string Key);

    private static IReadOnlyList<Reference> References()
    {
        var found = new List<Reference>();
        foreach (var file in AllXaml())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in Regex.Matches(lines[i], @"\{(StaticResource|DynamicResource)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}"))
                    found.Add(new Reference(Path.GetFileName(file), i + 1, m.Groups[1].Value, m.Groups[2].Value));
        }
        return found;
    }

    /// <summary>
    /// Pre-existing unresolved keys. Emptied 2026-09-17: the four near-miss brush names were repointed at the
    /// equivalent theme keys that already existed, and the four missing dialog styles were defined in
    /// ControlStyles.xaml. Keep this empty - it exists so a NEW unresolved key can be recorded deliberately
    /// rather than silently tolerated.
    /// </summary>
    private static readonly HashSet<string> KnownUnresolved = new(StringComparer.Ordinal);

    [Fact]
    public void EveryResourceReference_Should_ResolveToADefinedKey()
    {
        var defined = DefinedKeys();

        var unresolved = References()
            .Where(r => !defined.Contains(r.Key) && !KnownUnresolved.Contains(r.Key))
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line)
            .ToList();

        Assert.True(unresolved.Count == 0,
            $"{unresolved.Count} NEW resource reference(s) resolve to nothing:{Environment.NewLine}" +
            string.Join(Environment.NewLine,
                unresolved.Select(r => $"    {r.File}:{r.Line} {{{r.Kind} {r.Key}}}")));
    }

    [Fact]
    public void NoStaticResourceReference_Should_BeUnresolved()
    {
        var defined = DefinedKeys();

        // StaticResource is the severe half: an unresolved one throws XamlParseException when the view
        // loads, so the window simply fails to open. It is never acceptable, not even as known debt.
        var broken = References()
            .Where(r => r.Kind == "StaticResource" && !defined.Contains(r.Key))
            .ToList();

        Assert.True(broken.Count == 0,
            $"{broken.Count} StaticResource reference(s) would throw at view load:{Environment.NewLine}" +
            string.Join(Environment.NewLine, broken.Select(r => $"    {r.File}:{r.Line} {r.Key}")));
    }

    [Fact]
    public void KnownUnresolvedList_Should_NotContainKeysThatNowResolve()
    {
        var defined = DefinedKeys();
        var nowDefined = KnownUnresolved.Where(defined.Contains).ToList();

        // Keeps the debt list honest: a key left here after being defined would mask a future break.
        Assert.True(nowDefined.Count == 0,
            $"These keys are now defined and must be removed from KnownUnresolved: {string.Join(", ", nowDefined)}");
    }

    [Fact]
    public void Report_ResourceUsageShape()
    {
        var refs = References();
        var defined = DefinedKeys();

        // Not an assertion about style - just proves the scanner sees a realistic amount of the app,
        // so a silently-empty scan can never masquerade as "everything resolves".
        Assert.True(defined.Count > 200, $"Only {defined.Count} keys found; the scanner is probably not reading the app.");
        Assert.True(refs.Count > 1000, $"Only {refs.Count} references found; the scanner is probably not reading the app.");
    }
}
