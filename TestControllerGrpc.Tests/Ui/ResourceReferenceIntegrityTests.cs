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

    /// <summary>
    /// A separate Window does not see MainWindow.Resources: "defined somewhere in the app" is not enough.
    /// PipelineBuilderWindow used MainWindow's SmBtn and crashed on open (2026-11-09).
    /// </summary>
    [Fact]
    public void StaticResource_Should_ResolveInsideItsOwnWindow_When_TheViewIsASeparateWindow()
    {
        var appWide = AppWideKeys();
        var broken = new List<string>();

        foreach (var file in AllXaml().Where(IsWindow))
        {
            var text = File.ReadAllText(file);
            var own = KeysIn(text);
            foreach (var merged in MergedDictionaryFiles(text, file))
                own.UnionWith(KeysIn(File.ReadAllText(merged)));

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in Regex.Matches(lines[i], @"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}"))
                    if (!appWide.Contains(m.Groups[1].Value) && !own.Contains(m.Groups[1].Value))
                        broken.Add($"    {Path.GetFileName(file)}:{i + 1} {m.Groups[1].Value}");
        }

        Assert.True(broken.Count == 0,
            $"{broken.Count} StaticResource reference(s) are defined only in ANOTHER window and throw when this one opens:" +
            Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    private static bool IsWindow(string file) =>
        Regex.IsMatch(File.ReadAllText(file), @"^\s*(<\?xml[^>]*>\s*)?(<!--.*?-->\s*)*<Window\b", RegexOptions.Singleline);

    private static HashSet<string> KeysIn(string xaml) =>
        Regex.Matches(xaml, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>App.xaml, the dictionaries it merges, and keys injected from code-behind.</summary>
    private static HashSet<string> AppWideKeys()
    {
        var appXaml = Path.Combine(RepoRoot(), "TestControllerGrpc", "App.xaml");
        var text = File.ReadAllText(appXaml);
        var keys = KeysIn(text);
        foreach (var merged in MergedDictionaryFiles(text, appXaml))
            keys.UnionWith(KeysIn(File.ReadAllText(merged)));
        foreach (var file in AllCSharp())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Resources\\[\\s*\"([^\"]+)\"\\s*\\]"))
                keys.Add(m.Groups[1].Value);
        return keys;
    }

    private static IEnumerable<string> MergedDictionaryFiles(string xaml, string owner)
    {
        var project = Path.Combine(RepoRoot(), "TestControllerGrpc");
        foreach (Match m in Regex.Matches(xaml, "<ResourceDictionary\\s+Source=\"([^\"]+)\""))
        {
            var source = Regex.Replace(m.Groups[1].Value, @"^(pack://application:,,,)?/?(TestControllerGrpc;component/)?", "")
                .Replace('/', Path.DirectorySeparatorChar);
            var candidates = new[] { Path.Combine(Path.GetDirectoryName(owner)!, source), Path.Combine(project, source) };
            var found = candidates.FirstOrDefault(File.Exists);
            Assert.True(found is not null, $"{Path.GetFileName(owner)} merges '{m.Groups[1].Value}', which was not found.");
            yield return found!;
        }
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

    /// <summary>
    /// A DataGridColumn is a DependencyObject, not a FrameworkElement, so it never joins the visual or
    /// logical tree. A theme swap is propagated by walking that tree, so a DynamicResource set on a column
    /// resolves once and then never updates - the column keeps the brush from whichever theme was loaded
    /// when it was first realised while the rest of the window repaints. Put the brush on
    /// ElementStyle/EditingElementStyle or CellStyle, which are applied to real in-tree elements.
    /// </summary>
    [Fact]
    public void NoDataGridColumn_Should_CarryADynamicResourceBrush()
    {
        // Opening tag only: a DynamicResource inside a nested CellTemplate is fine, because that
        // content IS instantiated in the tree.
        var openingTag = new Regex(@"<DataGrid\w*Column\b[^>]*>", RegexOptions.Singleline);
        var offenders = new List<string>();

        foreach (var file in AllXaml())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in openingTag.Matches(text))
            {
                if (!m.Value.Contains("{DynamicResource", StringComparison.Ordinal)) continue;
                var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"    {Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} DataGridColumn(s) set a brush via DynamicResource, which will not survive a " +
            $"theme change. Move it to ElementStyle/CellStyle:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
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
