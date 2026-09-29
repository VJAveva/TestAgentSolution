using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// Guards the per-theme WatchList XML highlighting.
///
/// The point of the feature is that a node reads the SAME colour in the tree and in the XML view, so
/// the load test alone is not enough - the parity test is what stops the two drifting. Everything
/// except <see cref="Definitions_Should_Load_When_ThemeIsAnyOfThree"/> is pure file analysis, so it
/// reads the .xshd and the themes off disk and cannot flake.
/// </summary>
public sealed class XmlHighlightingTests
{
    /// <summary>xshd colour name -> the theme brush key it must mirror.</summary>
    private static readonly (string XshdColor, string ThemeBrush)[] NodeColourMap =
    [
        ("NodeRoot",       "CRunBrush"),     // WatchList / WatchItem
        ("NodeEvent",      "CEventBrush"),
        ("NodeGroupSeq",   "CSeqBrush"),
        ("NodeGroupPar",   "CParBrush"),
        ("NodeInitialize", "CInitBrush"),
        ("NodeRef",        "CRefBrush"),     // Ref / Template
        ("ActionRun",      "CRunBrush"),
        ("ActionRemote",   "CRemoteBrush"),
        ("ActionMail",     "CEmailBrush"),
        ("Token",          "CInitBrush"),
        ("SecretValue",    "AccRed"),
        ("Comment",        "TextDimBrush"),
        ("AttributeName",  "TextMutedBrush"),
        ("AttributeValue", "TextDefaultBrush"),
        ("Punctuation",    "TextMutedBrush"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ThemeFileFor(string theme) => theme switch
    {
        "Light" => "LightTheme.xaml",
        "High Contrast" => "HighContrastTheme.xaml",
        _ => "DarkTheme.xaml",
    };

    private static string XshdPathFor(string theme)
    {
        var fileName = XmlHighlightingProvider.ResourceNameFor(theme)
            .Replace("TestControllerGrpc.Resources.", "", StringComparison.Ordinal);
        return Path.Combine(RepoRoot(), "TestControllerGrpc", "Resources", fileName);
    }

    private static XDocument Xshd(string theme) => XDocument.Load(XshdPathFor(theme));

    private static string ThemeXaml(string theme) => File.ReadAllText(
        Path.Combine(RepoRoot(), "TestControllerGrpc", "Themes", ThemeFileFor(theme)));

    private static string XshdForeground(XDocument doc, string colourName)
    {
        XNamespace ns = "http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008";
        var el = doc.Root!.Elements(ns + "Color")
            .FirstOrDefault(e => (string?)e.Attribute("name") == colourName);
        Assert.True(el is not null, $"<Color name=\"{colourName}\"> missing.");
        var fg = (string?)el!.Attribute("foreground");
        Assert.False(string.IsNullOrWhiteSpace(fg), $"{colourName} has no foreground.");
        return fg!;
    }

    private static string ThemeBrushValue(string xaml, string key)
    {
        var m = Regex.Match(xaml, $"x:Key=\"{key}\"\\s+Color=\"(#[0-9A-Fa-f]{{6,8}})\"");
        Assert.True(m.Success, $"{key} not found in theme.");
        var value = m.Groups[1].Value;

        // The .xshd stores 6-digit RGB, so the comparison is only sound while the brush is opaque.
        // A brush that gains real alpha must fail here rather than silently compare against nothing.
        Assert.True(value.Length == 9 && value.StartsWith("#FF", StringComparison.OrdinalIgnoreCase),
            $"{key} is not an opaque #FFRRGGBB brush ({value}); the .xshd cannot represent its alpha.");
        return "#" + value[3..];
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void Definitions_Should_Load_When_ThemeIsAnyOfThree(string theme)
    {
        var definition = XmlHighlightingProvider.ForTheme(theme);

        Assert.NotNull(definition);
        // Prove it is OUR definition and not AvalonEdit's built-in XML: the built-in has no NodeEvent.
        Assert.NotNull(definition!.GetNamedColor("NodeEvent"));
        Assert.NotNull(definition.GetNamedColor("SecretValue"));
    }

    /// <summary>
    /// The whole feature is "XML matches the tree". If a theme brush moves and the .xshd does not
    /// follow, the same node shows two different colours depending on where you look.
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void Definitions_Should_MirrorTheTreeBrushes_When_ThemeChanges(string theme)
    {
        var doc = Xshd(theme);
        var xaml = ThemeXaml(theme);

        foreach (var (colour, brush) in NodeColourMap)
        {
            Assert.Equal(
                ThemeBrushValue(xaml, brush).ToUpperInvariant(),
                XshdForeground(doc, colour).ToUpperInvariant());
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void Definitions_Should_EmphasiseTokensSecretsAndComments_When_ThemeIsAnyOfThree(string theme)
    {
        XNamespace ns = "http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008";
        var colours = Xshd(theme).Root!.Elements(ns + "Color")
            .ToDictionary(e => (string)e.Attribute("name")!, e => e);

        Assert.Equal("bold", (string?)colours["Token"].Attribute("fontWeight"));
        Assert.Equal("bold", (string?)colours["SecretValue"].Attribute("fontWeight"));
        Assert.Equal("italic", (string?)colours["Comment"].Attribute("fontStyle"));

        // Attribute names/values carry no emphasis - they are the quiet background the node types pop against.
        Assert.Null(colours["AttributeName"].Attribute("fontWeight"));
        Assert.Null(colours["AttributeValue"].Attribute("fontWeight"));
    }

    /// <summary>
    /// Rule order is load-bearing and silent when wrong: AvalonEdit offers alternatives in document
    /// order at the same offset, so a plain ActionGroup/Action rule placed first would swallow every
    /// Parallel / RunRemoteCommand / SendMail variant and the file would still load cleanly.
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void Definitions_Should_OfferDiscriminatedRulesFirst_When_ElementSharesAName(string theme)
    {
        var text = File.ReadAllText(XshdPathFor(theme));

        int Index(string colour) =>
            text.IndexOf($"<Rule color=\"{colour}\">", StringComparison.Ordinal);

        Assert.True(Index("NodeGroupPar") >= 0 && Index("NodeGroupPar") < Index("NodeGroupSeq"),
            "ActionGroup Parallel must be offered before the Sequential default.");
        Assert.True(Index("ActionRemote") >= 0 && Index("ActionRemote") < Index("ActionRun"),
            "RunRemoteCommand must be offered before the RunCommand default.");
        Assert.True(Index("ActionMail") >= 0 && Index("ActionMail") < Index("ActionRun"),
            "SendMail must be offered before the RunCommand default.");
    }

    [Fact]
    public void Provider_Should_MapEveryAvailableTheme_To_AnEmbeddedResource()
    {
        var csproj = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "TestControllerGrpc.csproj"));

        var resources = ThemeService.AvailableThemes
            .Select(XmlHighlightingProvider.ResourceNameFor)
            .ToList();

        // A theme silently sharing another theme's definition is the failure this catches.
        Assert.Equal(ThemeService.AvailableThemes.Length, resources.Distinct().Count());

        foreach (var theme in ThemeService.AvailableThemes)
        {
            Assert.True(File.Exists(XshdPathFor(theme)), $"{theme}: .xshd missing on disk.");

            // An .xshd that is not an EmbeddedResource loads fine in a test and is absent at runtime.
            var fileName = Path.GetFileName(XshdPathFor(theme));
            Assert.Contains($"<EmbeddedResource Include=\"Resources\\{fileName}\" />", csproj);
        }
    }

    /// <summary>
    /// Switching theme must actually change what the editor renders. Caching by resource name means a
    /// mapping bug would hand every theme the same definition and the reload would be a silent no-op.
    /// </summary>
    [Fact]
    public void Provider_Should_ReturnADistinctDefinition_ForEveryTheme()
    {
        var definitions = ThemeService.AvailableThemes
            .Select(t => XmlHighlightingProvider.ForTheme(t)!)
            .ToList();

        Assert.Equal(definitions.Count, definitions.Distinct().Count());

        var eventColours = definitions
            .Select(d => d.GetNamedColor("NodeEvent")!.Foreground!.ToString())
            .ToList();
        Assert.Equal(eventColours.Count, eventColours.Distinct().Count());
    }

    /// <summary>
    /// Every AvalonEdit host must follow the theme, and must unhook: ThemeService.ThemeChanged is a
    /// static event, so a subscriber that never unsubscribes keeps its Window alive for the process.
    /// These two modal editors are the only AvalonEdit surfaces left - the inline MainWindow panel was
    /// removed because its ribbon button was deleted in 5b2b4be, leaving it unreachable.
    /// </summary>
    [Theory]
    [InlineData("RawXmlEditorWindow.xaml.cs")]
    [InlineData("TemplateXmlEditorWindow.xaml.cs")]
    public void AvalonEditHosts_Should_ReloadHighlighting_When_ThemeChanges(string file)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", file));

        Assert.Contains("XmlHighlightingProvider", source);
        Assert.Contains("ThemeService.ThemeChanged +=", source);
        Assert.Contains("ThemeService.ThemeChanged -=", source);
    }
}
