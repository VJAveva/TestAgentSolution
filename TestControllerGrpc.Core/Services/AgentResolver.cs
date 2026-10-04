using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Extracts all agent names that a WatchItem will use
/// by walking the action tree recursively.
///
/// Resolves [_Variable] references using provided parameters
/// so locking works on actual agent hostnames, not variable names.
///
/// Pass the WatchList's templates whenever the caller has them: a pipeline built from shared
/// templates carries every AgentName behind a &lt;Ref&gt;, so without them this returns nothing
/// and the caller reserves no agents at all.
/// </summary>
public static class AgentResolver
{
    /// <summary>
    /// Returns all unique agent names used by a WatchItem's action tree.
    /// Resolves variable references from parameters.
    /// </summary>
    public static List<string> ExtractAgentNames(
        WatchItemConfig watchItem,
        Dictionary<string, string>? parameters = null,
        IReadOnlyList<TemplateConfig>? templates = null)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in watchItem.Events)
            CollectFromNodes(ev.Children, agents, parameters, templates, visited);

        return agents.ToList();
    }

    /// <summary>
    /// Same as above but works with EventConfig directly.
    /// </summary>
    public static List<string> ExtractAgentNames(
        EventConfig eventConfig,
        Dictionary<string, string>? parameters = null,
        IReadOnlyList<TemplateConfig>? templates = null)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromNodes(eventConfig.Children, agents, parameters, templates,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return agents.ToList();
    }

    /// <summary>
    /// Extracts agent names from a single ActionGroup (used by partial execution).
    /// </summary>
    public static List<string> ExtractAgentNames(
        ActionGroupConfig group,
        Dictionary<string, string>? parameters = null,
        IReadOnlyList<TemplateConfig>? templates = null)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFromNodes(group.Children, agents, parameters, templates,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return agents.ToList();
    }

    /// <summary>
    /// Extracts agent names from a Template's action list (used for direct template execution).
    /// </summary>
    public static List<string> ExtractAgentNames(
        TemplateConfig template,
        Dictionary<string, string>? parameters = null,
        IReadOnlyList<TemplateConfig>? templates = null)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Seeded with this template so a self-reference cannot recurse back into it.
        CollectFromNodes(template.Children, agents, parameters, templates,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { template.ID });
        return agents.ToList();
    }

    /// <summary>
    /// Extracts the agent name from a single Action (used by partial execution).
    /// </summary>
    public static List<string> ExtractAgentNames(
        ActionConfig action,
        Dictionary<string, string>? parameters = null)
    {
        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(action.AgentName))
        {
            var resolved = ResolveVariable(action.AgentName, parameters);
            if (!string.IsNullOrEmpty(resolved))
                agents.Add(resolved);
        }
        return agents.ToList();
    }

    private static void CollectFromNodes(
        IReadOnlyList<IActionNode> nodes,
        HashSet<string> agents,
        Dictionary<string, string>? parameters,
        IReadOnlyList<TemplateConfig>? templates,
        HashSet<string> visitedTemplates)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionConfig action
                    when !string.IsNullOrEmpty(action.AgentName):
                {
                    var resolved = ResolveVariable(action.AgentName, parameters);
                    if (!string.IsNullOrEmpty(resolved))
                        agents.Add(resolved);
                    break;
                }

                case ActionGroupConfig group:
                    CollectFromNodes(group.Children, agents, parameters, templates, visitedTemplates);
                    break;

                case RefConfig reference when templates is not null:
                {
                    // Expanding each template once is enough for a set of names, and it stops a
                    // template that references itself (directly or in a cycle) from recursing forever.
                    if (!visitedTemplates.Add(reference.TemplateID))
                        break;

                    var template = templates.FirstOrDefault(t =>
                        string.Equals(t.ID, reference.TemplateID, StringComparison.OrdinalIgnoreCase));
                    if (template is not null)
                        CollectFromNodes(template.Children, agents, parameters, templates, visitedTemplates);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Resolves [_VariableName] to its value from parameters.
    /// Returns the original string if not a variable or not found.
    /// </summary>
    private static string ResolveVariable(
        string value,
        Dictionary<string, string>? parameters)
    {
        if (parameters == null) return value;

        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            var varName = value[1..^1];
            if (parameters.TryGetValue(varName, out var resolved))
                return resolved;

            if (varName.StartsWith('_'))
            {
                var withoutUnderscore = varName[1..];
                if (parameters.TryGetValue(withoutUnderscore, out resolved))
                    return resolved;
            }
        }

        return value;
    }
}
