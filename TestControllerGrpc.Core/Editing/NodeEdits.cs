using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Editing;

/// <summary>
/// Structural edits on one child list (ContextMenu_Requirement.md 2.5 / 2.6). Pure model operations; the caller
/// runs them inside an undoable edit and refreshes the tree.
/// </summary>
public static class NodeEdits
{
    /// <summary>Deep copy through the same XML writer Save uses, so a copy can never hold state Save would drop.</summary>
    public static IActionNode Clone(IActionNode node) => FromXml(ToXml(node));

    /// <summary>Clipboard form of a node. Kept in the app, never on the OS clipboard (actions can carry credentials).</summary>
    public static string ToXml(IActionNode node) =>
        WatchListXmlParser.SerializeTemplateList([new TemplateConfig { ID = "clip", Children = [node] }]);

    public static IActionNode FromXml(string xml) =>
        WatchListXmlParser.DeserializeTemplateList(xml) is [{ Children: [var node] }]
            ? node
            : throw new FormatException("Clipboard does not hold exactly one node.");

    /// <summary>Inserts a copy right after <paramref name="index"/>, tagged "(copy)". Returns the copy's index.</summary>
    public static int Duplicate(List<IActionNode> list, int index)
    {
        var copy = Clone(list[index]);
        Retag(copy, " (copy)");
        list.Insert(index + 1, copy);
        return index + 1;
    }

    /// <summary>Inserts <paramref name="node"/> above or below <paramref name="index"/>. Returns its index.</summary>
    public static int InsertSibling(List<IActionNode> list, int index, IActionNode node, bool above)
    {
        var at = above ? index : index + 1;
        list.Insert(at, node);
        return at;
    }

    /// <summary>Replaces the node with a new Sequential group that holds it.</summary>
    public static ActionGroupConfig Wrap(List<IActionNode> list, int index, string tag = "NewGroup")
    {
        var group = new ActionGroupConfig { Tag = tag, ExecutionType = ExecutionMode.Sequential, Children = [list[index]] };
        list[index] = group;
        return group;
    }

    /// <summary>Replaces a group with its children, in order. Returns false when the node is not a group.</summary>
    public static bool Ungroup(List<IActionNode> list, int index)
    {
        if (list[index] is not ActionGroupConfig group) return false;
        list.RemoveAt(index);
        list.InsertRange(index, group.Children);
        return true;
    }

    /// <summary>A new template holding copies of the group's children, with an ID unique among <paramref name="existingIds"/>.</summary>
    public static TemplateConfig GroupAsTemplate(ActionGroupConfig group, IEnumerable<string> existingIds)
    {
        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        var baseId = string.IsNullOrWhiteSpace(group.Tag) ? "NewTemplate" : group.Tag.Trim();
        var id = baseId;
        for (var n = 2; taken.Contains(id); n++) id = $"{baseId}_{n}";
        return new TemplateConfig { ID = id, Children = group.Children.Select(Clone).ToList() };
    }

    private static void Retag(IActionNode node, string suffix)
    {
        switch (node)
        {
            case ActionGroupConfig g: g.Tag += suffix; break;
            case ActionConfig a: a.Tag = string.IsNullOrEmpty(a.Tag) ? a.Tag : a.Tag + suffix; break;
        }
    }
}
