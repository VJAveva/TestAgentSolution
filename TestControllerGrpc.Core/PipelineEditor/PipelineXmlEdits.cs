using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.PipelineEditor;

/// <summary>Result of an editor edit: the new text and the path of the node to select afterwards.</summary>
public sealed record PipelineXmlEditResult(string Xml, string SelectPath);

/// <summary>
/// Structural and attribute edits on the editor's XML text. Each edit splices only the characters of the nodes
/// involved, so everything else (comments, notes, formatting, line endings) is unchanged. A null result means the
/// edit does not apply.
/// </summary>
public static class PipelineXmlEdits
{
    public static PipelineXmlEditResult? Delete(string xml, string path) =>
        Edit(xml, path, (map, node) =>
        {
            if (path.Length == 0 || !IsStepOrBranch(node.Kind)) return null;
            var (start, end) = PipelineXmlText.LineBlock(xml, node.Start, node.End);
            return (xml.Remove(start, end - start), PipelinePaths.Parent(path) ?? "");
        });

    /// <summary>Swaps the node with its previous / next sibling of the same kind; the text between them stays put.</summary>
    public static PipelineXmlEditResult? Move(string xml, string path, bool up) =>
        Edit(xml, path, (map, node) =>
        {
            var siblings = SiblingsOf(map, node);
            var index = siblings.IndexOf(node);
            var target = up ? index - 1 : index + 1;
            if (index < 0 || target < 0 || target >= siblings.Count) return null;

            var (first, second) = up ? (siblings[target], node) : (node, siblings[target]);
            var text = xml[..first.Start] + xml[second.Start..second.End] + xml[first.End..second.Start]
                + xml[first.Start..first.End] + xml[second.End..];
            return (text, SiblingPath(path, target));
        });

    public static PipelineXmlEditResult? Duplicate(string xml, string path) =>
        Edit(xml, path, (map, node) =>
        {
            if (!IsStep(node.Kind)) return null;
            var copy = xml[node.Start..node.End];
            var indent = PipelineXmlText.IndentOf(xml, node.Start);
            var (_, blockEnd) = PipelineXmlText.LineBlock(xml, node.Start, node.End);
            var text = blockEnd > node.End
                ? xml.Insert(node.End, PipelineXmlText.NewLineOf(xml) + indent + copy)
                : xml.Insert(node.End, copy);
            return (text, SiblingPath(path, SiblingsOf(map, node).IndexOf(node) + 1));
        });

    /// <summary>Skip or include a node. Skip writes the same attributes the WatchList parser reads.</summary>
    public static PipelineXmlEditResult? SetSkip(string xml, string path, bool skip, string? reason, string? user, DateTimeOffset now) =>
        Edit(xml, path, (_, node) =>
        {
            if (node.Kind is not (NodeKinds.WatchItem or NodeKinds.Event or NodeKinds.ActionGroup or NodeKinds.Action))
                return null;
            var text = xml;
            void Set(string name, string? value) => text = PipelineXmlText.SetAttribute(text, node.Start, name, value);
            if (!skip)
            {
                Set("Skip", null);
                Set("SkipReason", null);
                Set("SkippedBy", null);
                Set("SkippedAtUtc", null);
            }
            else
            {
                Set("Skip", "true");
                Set("SkipReason", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
                Set("SkippedBy", string.IsNullOrWhiteSpace(user) ? null : user);
                Set("SkippedAtUtc", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }
            return (text, path);
        });

    public static PipelineXmlEditResult? SetExecutionType(string xml, string path, ExecutionMode mode) =>
        Edit(xml, path, (_, node) => node.Kind is NodeKinds.Event or NodeKinds.ActionGroup
            ? (PipelineXmlText.SetAttribute(xml, node.Start, "ExecutionType", mode.ToString()), path)
            : null);

    /// <summary>Sets one attribute; a null or empty value removes it.</summary>
    public static PipelineXmlEditResult? SetAttribute(string xml, string path, string name, string? value) =>
        Edit(xml, path, (_, node) =>
            (PipelineXmlText.SetAttribute(xml, node.Start, name, string.IsNullOrEmpty(value) ? null : value), path));

    /// <summary>True when the node has a sibling of its kind in that direction (drives Move Up / Move Down).</summary>
    public static bool CanMove(PipelineXmlMap map, string path, bool up)
    {
        if (map.Find(path) is not { } node) return false;
        var siblings = SiblingsOf(map, node);
        var index = siblings.IndexOf(node);
        return index >= 0 && (up ? index > 0 : index < siblings.Count - 1);
    }

    /// <summary>Parses editor text for reading; null when it is not well-formed.</summary>
    public static XDocument? TryParse(string xml)
    {
        try { return XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return null; }
    }

    private static PipelineXmlEditResult? Edit(
        string xml, string path, Func<PipelineXmlMap, PipelineXmlNode, (string Xml, string Select)?> change)
    {
        if (PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(path) is not { } node) return null;
        if (change(map, node) is not { } result || result.Xml == xml) return null;
        return new PipelineXmlEditResult(result.Xml, result.Select);
    }

    private static List<PipelineXmlNode> SiblingsOf(PipelineXmlMap map, PipelineXmlNode node)
    {
        var parentPath = PipelinePaths.Parent(node.Path);
        var candidates = parentPath is null ? [] : map.Find(parentPath)?.Children ?? map.Roots;
        return candidates.Where(c => IsStep(node.Kind) ? IsStep(c.Kind) : c.Kind == node.Kind).ToList();
    }

    private static bool IsStep(string kind) =>
        kind is NodeKinds.ActionGroup or NodeKinds.Action or NodeKinds.Initialize or NodeKinds.Ref;

    private static bool IsStepOrBranch(string kind) =>
        IsStep(kind) || kind is NodeKinds.Event or NodeKinds.WatchItem or NodeKinds.Template;

    private static string SiblingPath(string path, int index)
    {
        var slash = path.LastIndexOf('/');
        var segment = slash >= 0 ? path[(slash + 1)..] : path;
        var renamed = $"{segment[0]}{index}";
        return slash >= 0 ? $"{path[..slash]}/{renamed}" : renamed;
    }
}
