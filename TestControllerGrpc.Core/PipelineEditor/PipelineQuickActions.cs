using TestControllerGrpc.Models;

namespace TestControllerGrpc.PipelineEditor;

/// <summary>
/// Quick actions on the selected node (PipelineEditor_Requirement.md section 7). Like every editor edit they splice
/// only the affected text, so each one is a single undo step and comments elsewhere are kept.
/// </summary>
public static class PipelineQuickActions
{
    /// <summary>Restart as SampleWatchList.xml writes it: shutdown with a 30 s grace, reboot-aware, 5 min budget.</summary>
    public const string RestartCommand = "shutdown";
    public const string RestartParameters = "/r /t 30";

    /// <summary>A settle wait runs on the controller; Start-Sleep works without a console, unlike <c>timeout</c>.</summary>
    public const string WaitCommand = "powershell";
    public const int DefaultWaitSeconds = 240;

    /// <summary>On a step: a new action after it. On an event, group or template: a new action at the end.</summary>
    public static PipelineXmlEditResult? AddStep(string xml, string path)
    {
        if (PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(path) is not { } node) return null;
        var agent = Attribute(xml, node, "AgentName");
        var step = agent is null
            ? "<Action Tag=\"New step\" Type=\"RunCommand\" Command=\"\" />"
            : $"<Action Tag=\"New step\" Type=\"RunRemoteCommand\" AgentName=\"{Escape(agent)}\" Command=\"\" />";
        return node.Kind is NodeKinds.Event or NodeKinds.ActionGroup or NodeKinds.Template
            ? AppendChild(xml, node, step)
            : InsertAfter(xml, map, node, step);
    }

    /// <summary>Restart the step's agent after it (remote command steps only).</summary>
    public static PipelineXmlEditResult? AddRestartAfter(string xml, string path)
    {
        if (PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(path) is not { Kind: NodeKinds.Action } node
            || Attribute(xml, node, "AgentName") is not { } agent)
            return null;
        var step = $"<Action Tag=\"Restart {Escape(agent)}\" Type=\"RunRemoteCommand\" AgentName=\"{Escape(agent)}\" "
                   + $"Command=\"{RestartCommand}\" Parameters=\"{RestartParameters}\" IsReboot=\"true\" Timeout=\"300\" />";
        return InsertAfter(xml, map, node, step);
    }

    public static PipelineXmlEditResult? AddWaitAfter(string xml, string path, int seconds = DefaultWaitSeconds)
    {
        if (PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(path) is not { } node || !IsStep(node.Kind)) return null;
        var step = $"<Action Tag=\"Settle {seconds / 60.0:0.#} min\" Type=\"RunCommand\" Command=\"{WaitCommand}\" "
                   + $"Parameters=\"-NoProfile -Command Start-Sleep -Seconds {seconds}\" Timeout=\"{seconds + 60}\" />";
        return InsertAfter(xml, map, node, step);
    }

    /// <summary>Sets the agent of the action, or of every action in the group / event that already names one.</summary>
    public static PipelineXmlEditResult? ChangeAgent(string xml, string path, string agent)
    {
        if (string.IsNullOrWhiteSpace(agent) || PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(path) is not { } node)
            return null;
        var actions = Descendants(node).Where(n => n.Kind == NodeKinds.Action
                && (n == node || Attribute(xml, n, "AgentName") is not null))
            .OrderByDescending(n => n.Start)
            .ToList();
        if (actions.Count == 0) return null;
        var text = actions.Aggregate(xml, (current, action) => PipelineXmlText.SetAttribute(current, action.Start, "AgentName", agent.Trim()));
        return text == xml ? null : new PipelineXmlEditResult(text, path);
    }

    /// <summary>Replaces the action with a parallel group holding one copy per agent (same as the main tree's Apply to agents).</summary>
    public static PipelineXmlEditResult? ApplyToAgents(string xml, string path, IReadOnlyList<string> agents)
    {
        if (agents.Count == 0 || PipelineXmlMap.TryBuild(xml) is not { } map || map.Find(path) is not { Kind: NodeKinds.Action } node)
            return null;
        var indent = PipelineXmlText.IndentOf(xml, node.Start);
        var inner = indent + "  ";
        var newLine = PipelineXmlText.NewLineOf(xml);
        var action = xml[node.Start..node.End];
        var copies = agents.Select(a => inner + PipelineXmlText.SetAttribute(action, 0, "AgentName", a));
        var group = $"<ActionGroup Tag=\"{Escape(node.Name)} (all agents)\" ExecutionType=\"Parallel\">{newLine}"
                    + string.Join(newLine, copies) + newLine + indent + "</ActionGroup>";
        return new PipelineXmlEditResult(xml[..node.Start] + group + xml[node.End..], path);
    }

    private static PipelineXmlEditResult InsertAfter(string xml, PipelineXmlMap map, PipelineXmlNode node, string element)
    {
        var (_, blockEnd) = PipelineXmlText.LineBlock(xml, node.Start, node.End);
        var text = blockEnd > node.End
            ? xml.Insert(node.End, PipelineXmlText.NewLineOf(xml) + PipelineXmlText.IndentOf(xml, node.Start) + element)
            : xml.Insert(node.End, element);
        var siblings = Siblings(map, node);
        return new PipelineXmlEditResult(text, Sibling(node.Path, siblings.IndexOf(node) + 1));
    }

    private static PipelineXmlEditResult AppendChild(string xml, PipelineXmlNode container, string element)
    {
        var indent = PipelineXmlText.IndentOf(xml, container.Start);
        var newLine = PipelineXmlText.NewLineOf(xml);
        var tagEnd = PipelineXmlText.StartTagEnd(xml, container.Start);
        var childPath = PipelinePaths.Child(container.Path, container.Children.Count(c => IsStep(c.Kind)));
        if (xml[tagEnd - 2] == '/')
        {
            // <Event ... /> becomes <Event ...> step </Event>.
            var open = xml[container.Start..(tagEnd - 2)].TrimEnd() + ">";
            var name = open[1..].Split([' ', '\t', '\r', '\n', '>'], 2)[0];
            var expanded = open + newLine + indent + "  " + element + newLine + indent + $"</{name}>";
            return new PipelineXmlEditResult(xml[..container.Start] + expanded + xml[tagEnd..], childPath);
        }
        var close = xml.LastIndexOf("</", container.End - 1, StringComparison.Ordinal);
        var lineStart = xml.LastIndexOf('\n', close - 1) + 1;
        var text = string.IsNullOrWhiteSpace(xml[lineStart..close])
            ? xml.Insert(lineStart, indent + "  " + element + newLine)
            : xml.Insert(close, newLine + indent + "  " + element + newLine + indent);
        return new PipelineXmlEditResult(text, childPath);
    }

    private static List<PipelineXmlNode> Siblings(PipelineXmlMap map, PipelineXmlNode node)
    {
        var parentPath = PipelinePaths.Parent(node.Path);
        var candidates = parentPath is null ? [] : map.Find(parentPath)?.Children ?? map.Roots;
        return candidates.Where(c => IsStep(node.Kind) ? IsStep(c.Kind) : c.Kind == node.Kind).ToList();
    }

    private static string Sibling(string path, int index)
    {
        var slash = path.LastIndexOf('/');
        var segment = slash >= 0 ? path[(slash + 1)..] : path;
        return (slash >= 0 ? path[..(slash + 1)] : "") + $"{segment[0]}{index}";
    }

    private static IEnumerable<PipelineXmlNode> Descendants(PipelineXmlNode node) =>
        node.Children.SelectMany(Descendants).Prepend(node);

    private static string? Attribute(string xml, PipelineXmlNode node, string name) =>
        PipelineXmlText.Attributes(xml, node.Start).FirstOrDefault(a => a.Name == name)?.Value is { Length: > 0 } v ? v : null;

    private static bool IsStep(string kind) => kind is NodeKinds.ActionGroup or NodeKinds.Action or NodeKinds.Initialize or NodeKinds.Ref;

    private static string Escape(string value) => PipelineXmlText.Escape(value, '"');
}
