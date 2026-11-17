using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.PipelineEditor;

/// <summary>Result of an editor edit: the new text and the path of the node to select afterwards.</summary>
public sealed record PipelineXmlEditResult(string Xml, string SelectPath);

/// <summary>
/// Structural edits on the editor's XML text. Edits the <see cref="XDocument"/> with whitespace preserved, so XML
/// comments, notes and formatting survive; a null result means the edit does not apply (the text is unchanged).
/// </summary>
public static class PipelineXmlEdits
{
    public static PipelineXmlEditResult? Delete(string xml, string path) =>
        Edit(xml, path, (element, _) =>
        {
            if (!IsStepOrBranch(element) || path.Length == 0) return null;
            RemoveWithIndent(element);
            return PipelinePaths.Parent(path) ?? "";
        });

    public static PipelineXmlEditResult? Move(string xml, string path, bool up) =>
        Edit(xml, path, (element, _) =>
        {
            var siblings = SiblingsOf(element);
            var index = siblings.IndexOf(element);
            var target = up ? index - 1 : index + 1;
            if (index < 0 || target < 0 || target >= siblings.Count) return null;

            // Moving down is moving the next sibling up, so only one direction needs whitespace handling.
            var (moving, anchor) = up ? (element, siblings[target]) : (siblings[target], element);
            var indent = moving.PreviousNode as XText;
            moving.Remove();
            indent?.Remove();
            anchor.AddBeforeSelf(moving);
            if (indent is not null) anchor.AddBeforeSelf(new XText(indent.Value));
            return SiblingPath(path, target);
        });

    public static PipelineXmlEditResult? Duplicate(string xml, string path) =>
        Edit(xml, path, (element, _) =>
        {
            if (!IsStep(element)) return null;
            var copy = new XElement(element);
            element.AddAfterSelf(copy);
            if (element.PreviousNode is XText indent && string.IsNullOrWhiteSpace(indent.Value))
                element.AddAfterSelf(new XText(indent.Value));
            return SiblingPath(path, SiblingsOf(element).IndexOf(element) + 1);
        });

    /// <summary>Skip or include a node. Skip writes the same attributes the WatchList parser reads.</summary>
    public static PipelineXmlEditResult? SetSkip(string xml, string path, bool skip, string? reason, string? user, DateTimeOffset now) =>
        Edit(xml, path, (element, _) =>
        {
            if (element.Name.LocalName is not ("WatchItem" or "Event" or "ActionGroup" or "Action")) return null;
            if (!skip)
            {
                element.Attribute("Skip")?.Remove();
                element.Attribute("SkipReason")?.Remove();
                element.Attribute("SkippedBy")?.Remove();
                element.Attribute("SkippedAtUtc")?.Remove();
                return path;
            }
            element.SetAttributeValue("Skip", "true");
            element.SetAttributeValue("SkipReason", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
            element.SetAttributeValue("SkippedBy", string.IsNullOrWhiteSpace(user) ? null : user);
            element.SetAttributeValue("SkippedAtUtc", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return path;
        });

    public static PipelineXmlEditResult? SetExecutionType(string xml, string path, ExecutionMode mode) =>
        Edit(xml, path, (element, _) =>
        {
            if (element.Name.LocalName is not ("Event" or "ActionGroup")) return null;
            element.SetAttributeValue("ExecutionType", mode.ToString());
            return path;
        });

    /// <summary>True when the node has a step sibling in that direction (drives Move Up / Move Down).</summary>
    public static bool CanMove(XElement element, bool up)
    {
        var siblings = SiblingsOf(element);
        var index = siblings.IndexOf(element);
        return index >= 0 && (up ? index > 0 : index < siblings.Count - 1);
    }

    /// <summary>Parses editor text for reading; null when it is not well-formed.</summary>
    public static XDocument? TryParse(string xml)
    {
        try { return XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return null; }
    }

    private static PipelineXmlEditResult? Edit(string xml, string path, Func<XElement, XDocument, string?> change)
    {
        var document = TryParse(xml);
        if (document is null || PipelinePaths.Resolve(document, path) is not { } element) return null;
        var select = change(element, document);
        if (select is null) return null;
        var text = Serialize(document);
        return text == xml ? null : new PipelineXmlEditResult(text, select);
    }

    // NewLineHandling.None: XNode.ToString turns every "\n" into "\r\n", which would rewrite every line of an LF file.
    private static string Serialize(XDocument document)
    {
        var builder = new System.Text.StringBuilder();
        if (document.Declaration is not null) builder.Append(document.Declaration);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None,
            ConformanceLevel = ConformanceLevel.Fragment,
        };
        using (var writer = XmlWriter.Create(builder, settings))
        {
            foreach (var node in document.Nodes())
                node.WriteTo(writer);
        }
        return builder.ToString();
    }

    private static List<XElement> SiblingsOf(XElement element) => element.Name.LocalName switch
    {
        "Event" => element.Parent?.Elements("Event").ToList() ?? [],
        "WatchItem" => element.Parent?.Elements("WatchItem").ToList() ?? [],
        "Template" => element.Parent?.Elements("Template").ToList() ?? [],
        _ when element.Parent is { } parent => PipelineXmlMap.StepChildren(parent).ToList(),
        _ => [],
    };

    private static bool IsStep(XElement element) =>
        element.Name.LocalName is "ActionGroup" or "Action" or "Initialize" or "Ref";

    private static bool IsStepOrBranch(XElement element) =>
        IsStep(element) || element.Name.LocalName is "Event" or "WatchItem" or "Template";

    private static string SiblingPath(string path, int index)
    {
        var slash = path.LastIndexOf('/');
        var segment = slash >= 0 ? path[(slash + 1)..] : path;
        var renamed = $"{segment[0]}{index}";
        return slash >= 0 ? $"{path[..slash]}/{renamed}" : renamed;
    }

    private static void RemoveWithIndent(XElement element)
    {
        if (element.PreviousNode is XText indent && string.IsNullOrWhiteSpace(indent.Value))
            indent.Remove();
        element.Remove();
    }
}
