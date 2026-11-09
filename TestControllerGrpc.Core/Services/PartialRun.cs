using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// "Run from here to end" (ContextMenu_Requirement.md 2.5 / 2.6 and section 3): which steps of an Event come
/// BEFORE the start node in execution order. Initialize nodes are never skipped - they supply the parameters
/// the later steps resolve against.
/// </summary>
public static class PartialRun
{
    /// <summary>
    /// Steps of <paramref name="evt"/> to skip so the run starts at <paramref name="start"/>. Null when the start
    /// node is not in this event (the caller must refuse the run).
    /// </summary>
    public static IReadOnlySet<IActionNode>? StepsBefore(EventConfig evt, IActionNode start)
    {
        var skips = new HashSet<IActionNode>(ReferenceEqualityComparer.Instance);
        return Collect(evt.Children, start, skips) ? skips : null;
    }

    /// <summary>Adds every node before <paramref name="start"/> to <paramref name="skips"/>; true once start is reached.</summary>
    private static bool Collect(List<IActionNode> children, IActionNode start, HashSet<IActionNode> skips)
    {
        foreach (var child in children)
        {
            if (ReferenceEquals(child, start)) return true;
            if (child is ActionGroupConfig group && Contains(group, start))
                return Collect(group.Children, start, skips);
            if (child is not InitializeConfig) skips.Add(child);
        }
        return false;
    }

    private static bool Contains(ActionGroupConfig group, IActionNode target) =>
        group.Children.Any(c => ReferenceEquals(c, target) || (c is ActionGroupConfig g && Contains(g, target)));
}
