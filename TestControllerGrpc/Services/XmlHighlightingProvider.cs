using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace TestControllerGrpc.Services;

/// <summary>
/// Loads the per-theme WatchList XML highlighting definition from the embedded <c>.xshd</c> resources.
/// </summary>
/// <remarks>
/// One definition per theme rather than one definition recoloured at runtime: an
/// <see cref="IHighlightingDefinition"/> is shared and its <see cref="HighlightingColor"/> brushes are
/// frozen, so mutating it in place would race every open editor.
///
/// Loading is cached per theme because the definitions are immutable and AvalonEdit is happy to share
/// one across editors - the same way <c>HighlightingManager.Instance</c> hands out a single instance.
/// A failed load returns null so the caller can fall back to the built-in "XML" definition; a broken
/// resource must degrade to plain XML colouring, never take the editor down.
/// </remarks>
public static class XmlHighlightingProvider
{
    private const string ResourcePrefix = "TestControllerGrpc.Resources.WatchListXml.";

    private static readonly ConcurrentDictionary<string, IHighlightingDefinition?> Cache = new();

    /// <summary>Embedded resource name backing <paramref name="themeName"/>.</summary>
    /// <remarks>Theme names are the values in <see cref="ThemeService.AvailableThemes"/>.</remarks>
    public static string ResourceNameFor(string? themeName) => themeName switch
    {
        "Light" => ResourcePrefix + "Light.xshd",
        "High Contrast" => ResourcePrefix + "HighContrast.xshd",
        _ => ResourcePrefix + "Dark.xshd",
    };

    /// <summary>
    /// The definition for <paramref name="themeName"/>, or null when the resource is missing or malformed.
    /// </summary>
    public static IHighlightingDefinition? ForTheme(string? themeName) =>
        Cache.GetOrAdd(ResourceNameFor(themeName), static resource => Load(resource));

    /// <summary>
    /// The definition for <paramref name="themeName"/>, falling back to AvalonEdit's built-in XML
    /// definition. This is what editors should call - it never returns null.
    /// </summary>
    public static IHighlightingDefinition? ForThemeOrDefault(string? themeName) =>
        ForTheme(themeName) ?? HighlightingManager.Instance.GetDefinition("XML");

    private static IHighlightingDefinition? Load(string resourceName)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;

            using var reader = new XmlTextReader(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex) when (ex is IOException or XmlException or HighlightingDefinitionInvalidException)
        {
            return null;
        }
    }
}
