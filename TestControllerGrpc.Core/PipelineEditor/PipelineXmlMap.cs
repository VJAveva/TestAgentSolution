using System.IO;
using System.Xml;
using System.Xml.Linq;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.PipelineEditor;

/// <summary>One step-bearing element of an editor document, located by structural path and character range.</summary>
/// <param name="Path">Same scheme as <c>NodeAddressing</c> inside a pipeline (<c>e0/c2</c>); a whole WatchList prefixes
/// <c>w{i}/</c>, templates use <c>t{i}</c>. The pipeline root of a single-pipeline document is <c>""</c>.</param>
/// <param name="Start">Offset of the element's <c>&lt;</c>.</param>
/// <param name="End">Offset just past its closing <c>&gt;</c>.</param>
/// <param name="SearchText">Name, agent, command and template id, for the tree's search box.</param>
public sealed record PipelineXmlNode(
    string Path,
    string Kind,
    string Name,
    int Line,
    int Start,
    int End,
    bool IsSkipped,
    string SearchText,
    IReadOnlyList<PipelineXmlNode> Children);

/// <summary>
/// Maps editor XML text to its pipeline structure so the tree, the XML caret and the properties pane can share one
/// selection. Read-only; <see cref="PipelineXmlEdits"/> changes the text.
/// </summary>
public sealed class PipelineXmlMap
{
    private readonly List<PipelineXmlNode> _all = [];
    private readonly int[] _lineStarts;

    public IReadOnlyList<PipelineXmlNode> Roots { get; }

    private PipelineXmlMap(string xml, IReadOnlyList<PipelineXmlNode> roots)
    {
        Roots = roots;
        _lineStarts = LineStarts(xml);
        Collect(roots);

        void Collect(IEnumerable<PipelineXmlNode> nodes)
        {
            foreach (var node in nodes)
            {
                _all.Add(node);
                Collect(node.Children);
            }
        }
    }

    /// <summary>Null when the text is not well-formed XML.</summary>
    public static PipelineXmlMap? TryBuild(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument document;
        List<(int Start, int End)> spans;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            spans = ElementSpans(xml);
        }
        catch (XmlException)
        {
            return null;
        }
        if (document.Root is null) return null;

        // Document order of XDocument elements matches the reader's, so the n-th element owns the n-th span.
        var spanOf = new Dictionary<XElement, (int Start, int End)>();
        var index = 0;
        foreach (var element in document.Root.DescendantsAndSelf())
            spanOf[element] = spans[index++];

        var lineStarts = LineStarts(xml);
        PipelineXmlNode Node(XElement element, string path, IReadOnlyList<PipelineXmlNode> children)
        {
            var (start, end) = spanOf[element];
            var skipped = string.Equals(element.Attribute("Skip")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            var search = string.Join(" ", new[] { "Tag", "AgentName", "Command", "TemplateID", "Type", "ID" }
                .Select(a => element.Attribute(a)?.Value).Where(v => !string.IsNullOrEmpty(v)));
            return new PipelineXmlNode(path, KindOf(element), NameOf(element), LineOf(lineStarts, start), start, end,
                skipped, search, children);
        }

        IReadOnlyList<PipelineXmlNode> Steps(XElement parent, string parentPath) =>
            StepChildren(parent).Select((child, i) =>
            {
                var path = PipelinePaths.Child(parentPath, i);
                return Node(child, path,
                    child.Name.LocalName == "ActionGroup" ? Steps(child, path) : []);
            }).ToList();

        IReadOnlyList<PipelineXmlNode> Events(XElement item, string itemPath) =>
            item.Elements("Event").Select((evt, i) =>
            {
                var path = PipelinePaths.Event(itemPath, i);
                return Node(evt, path, Steps(evt, path));
            }).ToList();

        IReadOnlyList<PipelineXmlNode> Templates(XElement templates) =>
            templates.Elements("Template").Select((template, i) =>
            {
                var path = PipelinePaths.Template(i);
                return Node(template, path, Steps(template, path));
            }).ToList();

        var root = document.Root;
        IReadOnlyList<PipelineXmlNode> roots = root.Name.LocalName switch
        {
            "WatchItem" => [Node(root, "", Events(root, ""))],
            "WatchList" => root.Elements("WatchItem")
                .Select((item, i) => Node(item, PipelinePaths.Item(i), Events(item, PipelinePaths.Item(i))))
                .Concat(root.Elements("Templates").SelectMany(Templates))
                .ToList(),
            "Templates" => Templates(root),
            _ => [],
        };
        return new PipelineXmlMap(xml, roots);
    }

    /// <summary>Steps of an Event, ActionGroup or Template. A <c>&lt;Comment&gt;</c> child is a note, not a step.</summary>
    public static IEnumerable<XElement> StepChildren(XElement parent) =>
        parent.Elements().Where(e => e.Name.LocalName is "ActionGroup" or "Action" or "Initialize" or "Ref");

    public PipelineXmlNode? Find(string? path) =>
        path is null ? null : _all.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.Ordinal));

    /// <summary>The deepest node whose element contains <paramref name="offset"/>.</summary>
    public PipelineXmlNode? NodeAt(int offset) =>
        _all.Where(n => n.Start <= offset && offset < n.End).MaxBy(n => n.Start);

    /// <summary>Node for a diagnostic position (1-based line and column, as the analyzer reports them).</summary>
    public PipelineXmlNode? NodeAt(int line, int column)
    {
        if (line < 1 || line > _lineStarts.Length) return null;
        return NodeAt(_lineStarts[line - 1] + Math.Max(0, column - 1));
    }

    public IReadOnlyList<PipelineXmlNode> All => _all;

    private static string KindOf(XElement element) => element.Name.LocalName switch
    {
        "WatchItem" => NodeKinds.WatchItem,
        "Event" => NodeKinds.Event,
        "ActionGroup" => NodeKinds.ActionGroup,
        "Action" => NodeKinds.Action,
        "Initialize" => NodeKinds.Initialize,
        "Ref" => NodeKinds.Ref,
        "Template" => NodeKinds.Template,
        var other => other,
    };

    private static string NameOf(XElement element)
    {
        string? A(string name) => element.Attribute(name)?.Value is { Length: > 0 } v ? v : null;
        return element.Name.LocalName switch
        {
            "WatchItem" or "ActionGroup" => A("Tag") ?? element.Name.LocalName,
            "Event" => $"{A("Type") ?? "Event"} ({A("ExecutionType") ?? "Sequential"})",
            "Action" => A("Tag") ?? A("Command") ?? A("Title") ?? "Action",
            "Initialize" => A("Tag") ?? (Path.GetFileName(A("ParameterFile") ?? "") is { Length: > 0 } file ? file : "Initialize"),
            "Ref" => A("TemplateID") ?? "Ref",
            "Template" => A("ID") ?? "Template",
            var other => other,
        };
    }

    /// <summary>Exact character range of every element, in document order.</summary>
    private static List<(int Start, int End)> ElementSpans(string xml)
    {
        var lineStarts = LineStarts(xml);
        var spans = new List<(int Start, int End)>();
        var open = new Stack<int>();
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var info = (IXmlLineInfo)reader;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                // Line info points at the name, one past '<'.
                var start = lineStarts[info.LineNumber - 1] + info.LinePosition - 2;
                if (reader.IsEmptyElement)
                    spans.Add((start, TagEnd(xml, start)));
                else
                {
                    open.Push(spans.Count);
                    spans.Add((start, -1));
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var closeName = lineStarts[info.LineNumber - 1] + info.LinePosition - 1;
                var i = open.Pop();
                spans[i] = (spans[i].Start, xml.IndexOf('>', closeName) + 1);
            }
        }
        return spans;
    }

    /// <summary>Offset past the '&gt;' that ends the tag starting at <paramref name="start"/>; quoted values may hold '&gt;'.</summary>
    private static int TagEnd(string xml, int start)
    {
        char? quote = null;
        for (var i = start; i < xml.Length; i++)
        {
            var c = xml[i];
            if (quote is not null) { if (c == quote) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i + 1;
        }
        return xml.Length;
    }

    private static int[] LineStarts(string xml)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < xml.Length; i++)
            if (xml[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    private static int LineOf(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}

/// <summary>Builds and reads editor paths; one place so the map and the edits cannot disagree.</summary>
public static class PipelinePaths
{
    public static string Item(int index) => $"w{index}";
    public static string Template(int index) => $"t{index}";
    public static string Event(string itemPath, int index) => Join(itemPath, $"e{index}");
    public static string Child(string parentPath, int index) => Join(parentPath, $"c{index}");

    public static string? Parent(string path) =>
        path.LastIndexOf('/') is var i and >= 0 ? path[..i] : path.Length > 0 ? "" : null;

    /// <summary>Resolves a path to its element. The single-pipeline root has path <c>""</c>.</summary>
    public static XElement? Resolve(XDocument document, string path)
    {
        var current = document.Root;
        if (current is null) return null;
        if (path.Length == 0) return current;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length < 2 || !int.TryParse(segment[1..], out var index) || index < 0) return null;
            IEnumerable<XElement> candidates = segment[0] switch
            {
                'w' => current.Elements("WatchItem"),
                't' => (current.Name.LocalName == "Templates" ? current : current.Element("Templates"))?.Elements("Template") ?? [],
                'e' => current.Elements("Event"),
                'c' => PipelineXmlMap.StepChildren(current),
                _ => [],
            };
            current = candidates.ElementAtOrDefault(index);
            if (current is null) return null;
        }
        return current;
    }

    private static string Join(string parent, string segment) => parent.Length == 0 ? segment : $"{parent}/{segment}";
}
