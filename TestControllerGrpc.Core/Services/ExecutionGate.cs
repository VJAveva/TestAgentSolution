using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>What the executor should do with one node.</summary>
public sealed record ExecutionDecision(bool ShouldRun, SkipState Skip)
{
    public static readonly ExecutionDecision Run = new(true, SkipState.NotSkipped);

    public static ExecutionDecision Skipped(SkipState state) => new(false, state);
}

/// <summary>
/// Decides whether a node runs. Consulted by the executor immediately before dispatch.
/// </summary>
/// <remarks>
/// Enforcement lives here, in Core, at dispatch — not in the WPF tree and not in the React tree. Both front
/// doors and any future scheduled trigger get identical behaviour from one engine. A gate implemented in a UI
/// is a gate that the other UI does not have.
/// </remarks>
public interface IExecutionGate
{
    /// <summary>Decision for a node whose ancestors resolved to <paramref name="inherited"/>.</summary>
    ExecutionDecision Evaluate(ISkippableNode node, SkipState inherited);
}

/// <inheritdoc cref="IExecutionGate"/>
public sealed class ExecutionGate : IExecutionGate
{
    public ExecutionDecision Evaluate(ISkippableNode node, SkipState inherited)
    {
        ArgumentNullException.ThrowIfNull(node);

        // An inherited skip wins: a child of a skipped group cannot run, whatever its own flag says.
        if (inherited.IsSkipped)
            return ExecutionDecision.Skipped(inherited);

        if (node.Skip)
            return ExecutionDecision.Skipped(new SkipState(SkipOrigin.Explicit, node.SkipReason, null));

        return ExecutionDecision.Run;
    }
}

/// <summary>
/// Resolves the skip cascade top-down over a WatchItem tree, and produces the pre-run manifest.
/// </summary>
public static class SkipEvaluator
{
    /// <summary>The skip state a child inherits from a parent that resolved to <paramref name="parentState"/>.</summary>
    /// <remarks>
    /// Deeper descendants keep pointing at the ORIGINAL explicitly-skipped ancestor rather than their immediate
    /// parent: that is the node the user has to unskip, and naming the intermediate one sends them to a node
    /// whose own flag is already false.
    /// </remarks>
    public static SkipState Descend(SkipState parentState, ISkippableNode parent, string parentLabel)
    {
        // Already inherited: preserve the ancestor that is actually responsible.
        if (parentState.Origin == SkipOrigin.Inherited)
            return parentState;

        if (parentState.Origin == SkipOrigin.Explicit || parent.Skip)
            return new SkipState(SkipOrigin.Inherited, parentState.Reason ?? parent.SkipReason, parentLabel);

        return SkipState.NotSkipped;
    }

    /// <summary>
    /// Every node that will not run, in execution order, with the reason. Empty when the run is clean.
    /// </summary>
    public static IReadOnlyList<SkippedNode> BuildManifest(WatchItemConfig watchItem)
    {
        ArgumentNullException.ThrowIfNull(watchItem);

        var manifest = new List<SkippedNode>();

        SkipState watchItemState = watchItem.Skip
            ? new SkipState(SkipOrigin.Explicit, watchItem.SkipReason, null)
            : SkipState.NotSkipped;

        if (watchItemState.IsSkipped)
            manifest.Add(new SkippedNode("WatchItem", watchItem.Tag, watchItemState));

        foreach (EventConfig evt in watchItem.Events)
        {
            SkipState inherited = Descend(watchItemState, watchItem, $"WatchItem '{watchItem.Tag}'");
            SkipState eventState = Resolve(evt, inherited, $"Event '{evt.Type}'", "Event", evt.Type, manifest);

            foreach (IActionNode child in evt.Children)
                Walk(child, Descend(eventState, evt, $"Event '{evt.Type}'"), manifest);
        }

        return manifest;
    }

    private static void Walk(IActionNode node, SkipState inherited, List<SkippedNode> manifest)
    {
        switch (node)
        {
            case ActionGroupConfig group:
            {
                SkipState state = Resolve(group, inherited, $"ActionGroup '{group.Tag}'", "ActionGroup", group.Tag, manifest);
                SkipState childInherited = Descend(state, group, $"ActionGroup '{group.Tag}'");
                foreach (IActionNode child in group.Children)
                    Walk(child, childInherited, manifest);
                break;
            }
            case ActionConfig action:
                Resolve(action, inherited, DescribeAction(action), "Action", DescribeAction(action), manifest);
                break;
        }
    }

    private static SkipState Resolve(
        ISkippableNode node, SkipState inherited, string label, string kind, string name, List<SkippedNode> manifest)
    {
        if (inherited.IsSkipped)
        {
            manifest.Add(new SkippedNode(kind, name, inherited));
            return inherited;
        }

        if (node.Skip)
        {
            var state = new SkipState(SkipOrigin.Explicit, node.SkipReason, null);
            manifest.Add(new SkippedNode(kind, name, state));
            return state;
        }

        return SkipState.NotSkipped;
    }

    private static string DescribeAction(ActionConfig action) =>
        !string.IsNullOrWhiteSpace(action.Tag) ? action.Tag
        : !string.IsNullOrWhiteSpace(action.Command) ? action.Command
        : action.Type.ToString();
}

/// <summary>One entry in the pre-run skip manifest.</summary>
public sealed record SkippedNode(string NodeKind, string Name, SkipState State)
{
    /// <summary>
    /// True when skipping this node leaves the machine in an unknown state, so later phases run on a box that
    /// was never reset. The confirmation must shout about these rather than list them like any other step.
    /// </summary>
    public bool IsMachineStateOperation =>
        Name.Contains("revert", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("reboot", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("restore", StringComparison.OrdinalIgnoreCase);
}
