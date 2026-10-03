using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Expands <c>Ref</c> nodes into the template's actions, for DISPLAY only.
/// </summary>
/// <remarks>
/// A session's SnapshotNodes is a faithful clone of the authored tree, so a Ref stays a Ref -
/// templates are expanded later, by the executor. Every "what will this run do?" view therefore
/// walked straight past them and showed only the actions authored inline, which on a Ref-built
/// pipeline is almost nothing: a 4-minute revert displayed one pending pill and no agent rows.
///
/// Deliberately NOT applied to SnapshotNodes itself. That tree is frozen for retry and node
/// addressing, and reshaping it would change how nodes are identified.
/// </remarks>
public static class SnapshotExpander
{
    /// <summary>
    /// Returns the nodes with every Ref replaced by its template's children. Unknown templates are
    /// dropped - the executor logs and skips them, so previewing them would be a lie.
    /// </summary>
    public static List<IActionNode> ExpandForDisplay(
        IReadOnlyList<IActionNode>? nodes,
        IReadOnlyDictionary<string, TemplateConfig>? templates)
    {
        var result = new List<IActionNode>();
        if (nodes is null || nodes.Count == 0) return result;

        Walk(nodes, templates, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Leaf actions this run will perform, with templates expanded.</summary>
    public static int CountLeafActions(
        IReadOnlyList<IActionNode>? nodes,
        IReadOnlyDictionary<string, TemplateConfig>? templates)
    {
        var count = 0;
        CountLeaves(ExpandForDisplay(nodes, templates), ref count);
        return count;

        static void CountLeaves(IReadOnlyList<IActionNode> nodes, ref int count)
        {
            foreach (var n in nodes)
            {
                if (n is ActionConfig) count++;
                else if (n is ActionGroupConfig g) CountLeaves(g.Children, ref count);
            }
        }
    }

    private static void Walk(
        IReadOnlyList<IActionNode> nodes,
        IReadOnlyDictionary<string, TemplateConfig>? templates,
        List<IActionNode> into,
        HashSet<string> visiting)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case RefConfig reference:
                    if (templates is null) break;
                    if (!templates.TryGetValue(reference.TemplateID, out var template)) break;
                    // A template that Refs itself would otherwise recurse forever.
                    if (!visiting.Add(reference.TemplateID)) break;
                    Walk(template.Children, templates, into, visiting);
                    visiting.Remove(reference.TemplateID);
                    break;

                case ActionGroupConfig group:
                    var expanded = new List<IActionNode>();
                    Walk(group.Children, templates, expanded, visiting);
                    into.Add(new ActionGroupConfig
                    {
                        NodeId = group.NodeId,
                        Tag = group.Tag,
                        ExecutionType = group.ExecutionType,
                        FailAndContinue = group.FailAndContinue,
                        Children = expanded,
                        Skip = group.Skip,
                        SkipReason = group.SkipReason,
                    });
                    break;

                default:
                    into.Add(node);
                    break;
            }
        }
    }
}
