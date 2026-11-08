using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>
/// Masks secret values in what the builder shows a browser. Uses the parameter grid's key list
/// (<see cref="TokenDisplay.IsSecret"/>) so both surfaces hide the same things.
/// </summary>
/// <remarks>
/// Display only. The files Create writes are rebuilt on the server and keep the real values.
/// </remarks>
public static partial class BuilderRedaction
{
    public const string Mask = "***";

    [GeneratedRegex(@"^\s*(\[_\w+\]\s*)+$")]
    private static partial Regex TokenReferencesOnly();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Value(string key, string? value) =>
        TokenDisplay.IsSecret(key) && !string.IsNullOrEmpty(value) ? Mask : value ?? "";

    public static string ConfigJson(PipelineParameterConfig config)
    {
        var masked = new PipelineParameterConfig
        {
            Version = config.Version,
            Global = MaskLayer(config.Global),
            Profiles = config.Profiles.ToDictionary(p => p.Key, p => MaskLayer(p.Value), StringComparer.OrdinalIgnoreCase),
            Pipelines = config.Pipelines.ToDictionary(p => p.Key, p => MaskLayer(p.Value), StringComparer.OrdinalIgnoreCase),
        };
        return JsonSerializer.Serialize(masked, WriteOptions);
    }

    /// <summary>
    /// Masks every attribute or element whose NAME is secret. A value made only of token references
    /// (<c>[_RcloudPassword]</c>) carries no secret and stays visible, so the author can see the wiring.
    /// </summary>
    public static string WatchItemXml(WatchItemConfig item)
    {
        var doc = XDocument.Parse(WatchListXmlParser.SerializeWatchItemsToXml([item], []));
        foreach (var element in doc.Descendants())
        {
            foreach (var attribute in element.Attributes())
                if (ShouldMask(attribute.Name.LocalName, attribute.Value))
                    attribute.Value = Mask;

            if (!element.HasElements && ShouldMask(element.Name.LocalName, element.Value))
                element.Value = Mask;
        }
        return doc.ToString();
    }

    private static bool ShouldMask(string name, string value) =>
        TokenDisplay.IsSecret(name) && value.Length > 0 && !TokenReferencesOnly().IsMatch(value);

    private static Dictionary<string, string> MaskLayer(Dictionary<string, string> layer) =>
        layer.ToDictionary(e => e.Key, e => Value(e.Key, e.Value), StringComparer.OrdinalIgnoreCase);
}
