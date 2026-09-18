using System.IO;
using System.Xml.Linq;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-9 ratchet. A control with no accessible name is announced by a screen reader as just its type
/// ("button"), which makes it unusable. WPF derives a name from text content, so a button whose only
/// content is a glyph is silent - and an input control can never self-name, because its own value is
/// what the user typed, not what the field is for.
///
/// Pure file analysis, like the other Ui guards: no STA thread, no WPF instantiation.
/// </summary>
public sealed class AccessibleNameTests
{
    // 209 -> 0. Icon-only buttons take their name from the tooltip, inputs from the visible <Label>
    // that precedes them (which satisfies WCAG 2.5.3 Label in Name by construction), and the rest were
    // named by hand. Template parts inside a ControlTemplate are excluded - UIA surfaces the templated
    // parent's name, so they are not separately nameable. This is now a floor, not a ratchet.
    private const int NamelessBaseline = 0;

    private static readonly string[] Interactive =
    [
        "Button", "ToggleButton", "RepeatButton", "CheckBox", "RadioButton",
        "ComboBox", "TextBox", "PasswordBox", "DatePicker", "Slider",
        // Containers a screen reader announces as a region; without a name they are just "tree"/"list".
        "ListBox", "TreeView", "DataGrid",
    ];

    /// <summary>Controls whose own content is a VALUE, not a label - they need Name or LabeledBy.</summary>
    private static readonly string[] ValueOnly = ["TextBox", "PasswordBox", "ComboBox", "DatePicker", "Slider"];

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

    /// <summary>A glyph is not a name: UIA would announce the raw symbol, which conveys nothing.</summary>
    private static bool HasReadableText(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && s.Any(c => char.IsLetterOrDigit(c) && !(c >= '\uE000' && c <= '\uF8FF'));

    private static bool HasExplicitName(XElement e) =>
        e.Attributes().Any(a => a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy");

    /// <summary>
    /// Controls inside a ControlTemplate are template PARTS, not instances - UIA surfaces the templated
    /// parent's name, so naming a scrollbar's RepeatButton individually is neither possible nor useful.
    /// </summary>
    private static bool IsTemplatePart(XElement e) =>
        e.Ancestors().Any(a => a.Name.LocalName == "ControlTemplate");

    private static bool IsNameless(XElement e)
    {
        if (HasExplicitName(e)) return false;
        if (ValueOnly.Contains(e.Name.LocalName)) return true;

        var text = string.Join(
            " ",
            e.DescendantsAndSelf()
                .SelectMany(d => d.Attributes())
                .Where(a => a.Name.LocalName is "Content" or "Text" or "Header")
                .Select(a => a.Value)
                .Concat(e.DescendantsAndSelf().Where(d => d.Name.LocalName == "Run").Select(d => d.Value)));

        // A binding resolves to real text at runtime, so it counts as a name.
        if (text.Contains("{Binding") || text.Contains("{TemplateBinding")) return false;

        return !HasReadableText(text);
    }

    private static (int Total, string Breakdown) CountNameless()
    {
        var perFile = new List<(string File, int Count)>();
        var total = 0;

        foreach (var file in ViewFiles())
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }

            var n = doc.Descendants()
                .Where(e => Interactive.Contains(e.Name.LocalName) && !IsTemplatePart(e))
                .Count(IsNameless);

            if (n == 0) continue;
            perFile.Add((Path.GetFileName(file), n));
            total += n;
        }

        var breakdown = string.Join(
            Environment.NewLine,
            perFile.OrderByDescending(x => x.Count).Take(10).Select(x => $"    {x.File}: {x.Count}"));
        return (total, breakdown);
    }

    [Fact]
    public void NamelessControls_Should_NotIncrease_When_ViewsChange()
    {
        var (total, breakdown) = CountNameless();

        Assert.True(total <= NamelessBaseline,
            $"Controls with no accessible name rose to {total} (baseline {NamelessBaseline}). "
            + $"Add AutomationProperties.Name (or LabeledBy for inputs).{Environment.NewLine}"
            + $"Top files:{Environment.NewLine}{breakdown}");
    }

    /// <summary>
    /// MainWindow's toolbar is entirely icon-only buttons - the case where a missing name is total
    /// silence rather than a poor name. Pins the batch that was labelled so it cannot be reverted.
    /// </summary>
    [Fact]
    public void MainWindow_Should_NameItsIconOnlyCommandButtons_When_ToolbarIsRendered()
    {
        var path = Path.Combine(RepoRoot(), "TestControllerGrpc", "Views", "MainWindow.xaml");
        var named = XDocument.Load(path).Descendants()
            .Count(e => e.Name.LocalName is "Button" or "ToggleButton" && HasExplicitName(e));

        Assert.True(named >= 43, $"MainWindow named buttons fell to {named}; expected at least 43.");
    }
}
