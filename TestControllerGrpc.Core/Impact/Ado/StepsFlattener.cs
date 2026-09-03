using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TestControllerGrpc.Core.Impact.Ado;

/// <summary>
/// Flattens the Microsoft.VSTS.TCM.Steps XML (HTML-encoded action/expected fragments) into plain text:
/// action and expected joined with " -&gt; " per step, steps joined by newline. Never throws — malformed
/// XML yields null.
/// </summary>
public static partial class StepsFlattener
{
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    /// <summary>Returns flattened step text, or null when the input is empty or not parseable.</summary>
    public static string? Flatten(string? stepsXml)
    {
        if (string.IsNullOrWhiteSpace(stepsXml)) return null;
        try
        {
            var doc = XDocument.Parse(stepsXml);
            var lines = new List<string>();
            foreach (var step in doc.Descendants("step"))
            {
                var parts = step.Elements("parameterizedString")
                    .Select(p => StripTags(p.Value))
                    .Where(s => s.Length > 0)
                    .ToList();
                if (parts.Count > 0) lines.Add(string.Join(" -> ", parts));
            }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Strips HTML tags, decodes entities and collapses whitespace. Returns "" for null/empty.</summary>
    public static string StripTags(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var noTags = Tags().Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return Whitespace().Replace(decoded, " ").Trim();
    }
}
