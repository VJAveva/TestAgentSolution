using System.Security.Cryptography;
using System.Text;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>What a node-run should include beyond the node itself.</summary>
public enum NodeRunScope
{
    /// <summary>Run the node in isolation — nothing before or after it.</summary>
    OnlyThisNode = 0,

    /// <summary>Run the nearest owning Initialize first, then the node.</summary>
    NodeWithInitialize = 1,
}

/// <summary>The kinds of node a caller may run on their own.</summary>
public enum RunnableNodeKind
{
    Event,
    Group,
    Action,
    Template,
}

/// <summary>A node located by <see cref="NodeAddressing.TryResolve"/>.</summary>
public sealed record ResolvedNode(
    string Path,
    RunnableNodeKind Kind,
    EventConfig OwningEvent,
    IActionNode? Node,
    string DisplayName);

/// <summary>
/// Addresses nodes inside a <see cref="WatchItemConfig"/> by structural path.
///
/// <para>
/// <see cref="IActionNode.NodeId"/> cannot be used for this: it is <c>[JsonIgnore]</c>, is never
/// written to or read from WatchList.xml, and is regenerated on every parse — so it differs
/// between hosts and changes on every hot-reload. A structural path is derived from the same XML
/// on both sides and needs no file-format change. It is paired with <see cref="RevisionOf"/>
/// because WatchList.xml hot-reloads: a path captured at render time can otherwise point at a
/// different node by the time the user clicks Run.
/// </para>
/// </summary>
public static class NodeAddressing
{
    private const char SegmentSeparator = '/';
    private const string EventPrefix = "e";
    private const string ChildPrefix = "c";

    // ── Path construction ────────────────────────────────────────────────

    /// <summary>Path of the event at <paramref name="eventIndex"/>, e.g. "e0".</summary>
    public static string EventPath(int eventIndex) => EventPrefix + eventIndex.ToString();

    /// <summary>Path of a child at <paramref name="childIndex"/> under <paramref name="parentPath"/>, e.g. "e0/c2".</summary>
    public static string ChildPath(string parentPath, int childIndex) =>
        parentPath + SegmentSeparator + ChildPrefix + childIndex.ToString();

    /// <summary>
    /// Path of <paramref name="node"/> within <paramref name="item"/>, or null when the node is
    /// not part of that tree. Compares by reference, so a deep-cloned snapshot never matches.
    /// </summary>
    public static string? PathOf(WatchItemConfig item, IActionNode node)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(node);

        for (var e = 0; e < item.Events.Count; e++)
        {
            var found = FindIn(item.Events[e].Children, EventPath(e), node);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? FindIn(List<IActionNode> children, string parentPath, IActionNode target)
    {
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var path = ChildPath(parentPath, i);
            if (ReferenceEquals(child, target)) return path;
            if (child is ActionGroupConfig group)
            {
                var found = FindIn(group.Children, path, target);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ── Resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a structural path to a runnable node. Returns false with a caller-safe
    /// <paramref name="error"/> when the path is malformed, out of range, or names a node that
    /// cannot be run on its own (Initialize is setup, not a run target).
    /// </summary>
    public static bool TryResolve(
        WatchItemConfig item, string? path, out ResolvedNode? resolved, out string error)
    {
        ArgumentNullException.ThrowIfNull(item);
        resolved = null;

        if (!TryParse(path, out var indices, out error))
            return false;

        var eventIndex = indices[0];
        if (eventIndex >= item.Events.Count)
        {
            error = $"Event index {eventIndex} is out of range (the pipeline has {item.Events.Count} event(s)).";
            return false;
        }

        var evt = item.Events[eventIndex];
        if (indices.Count == 1)
        {
            resolved = new ResolvedNode(NormalizePath(indices), RunnableNodeKind.Event, evt, null, evt.Type);
            return true;
        }

        var children = evt.Children;
        IActionNode? node = null;
        for (var depth = 1; depth < indices.Count; depth++)
        {
            var childIndex = indices[depth];
            if (children is null || childIndex >= children.Count)
            {
                error = $"Node path '{path}' does not exist in this pipeline.";
                return false;
            }

            node = children[childIndex];
            children = (node as ActionGroupConfig)?.Children;
        }

        var normalized = NormalizePath(indices);
        switch (node)
        {
            case ActionGroupConfig group:
                resolved = new ResolvedNode(normalized, RunnableNodeKind.Group, evt, group,
                    string.IsNullOrWhiteSpace(group.Tag) ? "Group" : group.Tag);
                return true;
            case ActionConfig action:
                resolved = new ResolvedNode(normalized, RunnableNodeKind.Action, evt, action, action.ResolvedTag);
                return true;
            case RefConfig refNode:
                resolved = new ResolvedNode(normalized, RunnableNodeKind.Template, evt, refNode,
                    string.IsNullOrWhiteSpace(refNode.TemplateID) ? "Template" : refNode.TemplateID);
                return true;
            case InitializeConfig:
                error = "Initialize is a setup step and cannot be run on its own. "
                      + "Run a sibling node with the 'include Initialize' scope instead.";
                return false;
            default:
                error = $"Node path '{path}' names a node that cannot be run on its own.";
                return false;
        }
    }

    /// <summary>True when the path is well-formed and names a node that can be run on its own.</summary>
    public static bool IsRunnable(WatchItemConfig item, string? path) =>
        TryResolve(item, path, out _, out _);

    /// <summary>
    /// The session EventType the executor will use for this node.
    /// </summary>
    /// <remarks>
    /// <c>ExecutionSessionManager.BeginSession</c> returns a pre-registered session unchanged, so a
    /// caller that pre-creates the session must supply the SAME EventType the executor would — the
    /// <c>Action:</c>/<c>Group:</c>/<c>Template:</c> prefix is what
    /// <see cref="ExecutionSession.IsFullPipelineRun"/> keys off, and getting it wrong would make a
    /// scoped run report itself as a full pipeline run to every downstream consumer.
    /// </remarks>
    public static string SessionEventTypeFor(ResolvedNode resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.Kind switch
        {
            RunnableNodeKind.Event => resolved.OwningEvent.Type,
            RunnableNodeKind.Group => $"Group:{((ActionGroupConfig)resolved.Node!).Tag}",
            RunnableNodeKind.Action => $"Action:{((ActionConfig)resolved.Node!).ResolvedTag}",
            RunnableNodeKind.Template => $"Group:{resolved.DisplayName}",
            _ => resolved.OwningEvent.Type,
        };
    }

    /// <summary>The nodes a run of <paramref name="resolved"/> will execute, for progress pre-population.</summary>
    public static List<IActionNode> RunChildrenOf(ResolvedNode resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.Kind switch
        {
            RunnableNodeKind.Event => [.. resolved.OwningEvent.Children],
            RunnableNodeKind.Group => [.. ((ActionGroupConfig)resolved.Node!).Children],
            _ => resolved.Node is null ? [] : [resolved.Node],
        };
    }

    private static bool TryParse(string? path, out List<int> indices, out string error)
    {
        indices = [];
        error = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "A node path is required.";
            return false;
        }

        var segments = path.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "A node path is required.";
            return false;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var expected = i == 0 ? EventPrefix : ChildPrefix;
            var segment = segments[i];
            if (segment.Length < 2 || !segment.StartsWith(expected, StringComparison.Ordinal)
                || !int.TryParse(segment.AsSpan(1), out var index) || index < 0)
            {
                error = $"Malformed node path '{path}'. Expected the form 'e0/c2/c1'.";
                indices = [];
                return false;
            }
            indices.Add(index);
        }

        return true;
    }

    private static string NormalizePath(List<int> indices)
    {
        var sb = new StringBuilder(EventPrefix).Append(indices[0]);
        for (var i = 1; i < indices.Count; i++)
            sb.Append(SegmentSeparator).Append(ChildPrefix).Append(indices[i]);
        return sb.ToString();
    }

    // ── Initialize lookup ────────────────────────────────────────────────

    /// <summary>
    /// The Initialize node that owns <paramref name="path"/>, searching from the innermost
    /// containing group outward to the event. Returns null when no ancestor declares one, which
    /// is what lets the UI show the "+ its Initialize" option only where it means something.
    /// </summary>
    /// <remarks>
    /// The config tree has no parent pointers by design, so ancestry is recovered by walking the
    /// path down from the root and recording each container on the way.
    /// </remarks>
    public static InitializeConfig? NearestInitializeFor(WatchItemConfig item, string? path)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!TryParse(path, out var indices, out _)) return null;
        if (indices[0] >= item.Events.Count) return null;

        // An event has no ancestor to inherit from, and any Initialize it declares is already one of
        // its own children — so a run of the event always executes it. Offering it as an extra scope
        // would be a no-op the UI would have to explain.
        if (indices.Count == 1) return null;

        // Containers from outermost (the event) to innermost (the group directly holding the node).
        var containers = new List<List<IActionNode>> { item.Events[indices[0]].Children };
        var children = item.Events[indices[0]].Children;

        for (var depth = 1; depth < indices.Count; depth++)
        {
            var childIndex = indices[depth];
            if (children is null || childIndex >= children.Count) return null;

            // The final segment is the target itself, not a container to search.
            if (depth == indices.Count - 1) break;

            if (children[childIndex] is not ActionGroupConfig group) return null;
            containers.Add(group.Children);
            children = group.Children;
        }

        for (var i = containers.Count - 1; i >= 0; i--)
        {
            var init = containers[i].OfType<InitializeConfig>().FirstOrDefault();
            if (init is not null) return init;
        }
        return null;
    }

    // ── Revision ─────────────────────────────────────────────────────────

    /// <summary>
    /// A stable fingerprint of the WatchItem's shape. A client sends back the revision it rendered;
    /// the server rejects the run when it no longer matches, so a hot-reload that reshuffled the
    /// tree can never redirect a node-run onto a different node.
    /// </summary>
    /// <remarks>
    /// SHA-256 rather than <c>string.GetHashCode</c>, whose seed is randomized per process — two
    /// hosts (and two restarts of one host) must agree on the value.
    /// </remarks>
    public static string RevisionOf(WatchItemConfig item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sb = new StringBuilder();
        sb.Append("W:").Append(item.Tag).Append('|').Append(item.Events.Count).Append(';');
        for (var e = 0; e < item.Events.Count; e++)
        {
            var evt = item.Events[e];
            sb.Append("E").Append(e).Append(':').Append(evt.Type).Append('|')
              .Append(evt.ExecutionType).Append('|').Append(evt.Children.Count).Append(';');
            AppendNodes(sb, evt.Children);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static void AppendNodes(StringBuilder sb, List<IActionNode> children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            sb.Append(i).Append(':').Append(child.NodeType).Append('|');
            switch (child)
            {
                case ActionGroupConfig group:
                    sb.Append(group.Tag).Append('|').Append(group.ExecutionType).Append('|')
                      .Append(group.Children.Count).Append(';');
                    AppendNodes(sb, group.Children);
                    break;
                case ActionConfig action:
                    sb.Append(action.ResolvedTag).Append('|').Append(action.Type).Append('|')
                      .Append(action.AgentName).Append('|').Append(action.Command).Append(';');
                    break;
                case InitializeConfig init:
                    sb.Append(init.Tag).Append('|').Append(init.ParameterFile).Append('|')
                      .Append(init.Profile).Append(';');
                    break;
                case RefConfig refNode:
                    sb.Append(refNode.TemplateID).Append(';');
                    break;
                default:
                    sb.Append(';');
                    break;
            }
        }
    }
}
