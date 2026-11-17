using System.Collections.ObjectModel;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;

namespace TestControllerGrpc.ViewModels;

// PipelineEditor_Requirement.md Part 5 (section 7): quick actions on the selected node, shown as buttons above the
// form and in the structure menu. Each one is a single text edit, so one Ctrl+Z undoes it.
public sealed partial class WatchItemXmlEditorViewModel
{
    public ObservableCollection<NodeMenuItem> SelectedNodeActions { get; } = new();

    /// <summary>Asked before Revert drops every unsaved change in this tab; false keeps them.</summary>
    public Func<bool>? ConfirmRevert { get; set; }

    /// <summary>The buttons above the Properties form for this node.</summary>
    private IEnumerable<NodeMenuItem> QuickActionsFor(PipelineStructureNode node, IReadOnlyDictionary<string, string> attributes)
    {
        if (node.IsFromTemplate)
        {
            yield return Item(NodeMenuIds.EditTemplate, "Edit template", NodeMenuGroup.Edit, enabled: true);
            yield break;
        }
        if (node.Kind == NodeKinds.Ref)
            yield return Item(NodeMenuIds.EditTemplate, "Edit template", NodeMenuGroup.Edit, enabled: true);
        if (node.Kind == NodeKinds.Initialize)
        {
            var file = attributes.GetValueOrDefault("ParameterFile");
            yield return Item(PipelineEditorMenuIds.OpenParameterFile, "Open file", NodeMenuGroup.Edit,
                enabled: !string.IsNullOrWhiteSpace(file), tip: string.IsNullOrWhiteSpace(file) ? "No parameter file set." : null);
            yield return Item(PipelineEditorMenuIds.ReloadParameterFile, "Reload", NodeMenuGroup.Edit, enabled: true);
        }
        foreach (var item in EditorOnlyItems(node)) yield return item;
        if (node.Kind is NodeKinds.Event or NodeKinds.ActionGroup or NodeKinds.Action)
            yield return node.IsSkipped
                ? Item(NodeMenuIds.Include, "Unskip", NodeMenuGroup.State, CanEdit)
                : Item(NodeMenuIds.Skip, "Skip\u2026", NodeMenuGroup.State, CanEdit);
    }

    /// <summary>Editor-only items, also appended to the structure menu.</summary>
    private IEnumerable<NodeMenuItem> EditorOnlyItems(PipelineStructureNode node)
    {
        if (node.IsFromTemplate) yield break;
        var attributes = _map?.Find(node.Path) is { } mapNode
            ? PipelineXmlText.Attributes(XmlText, mapNode.Start).GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Value)
            : [];
        var agent = attributes.GetValueOrDefault("AgentName");
        var isStep = node.Kind is NodeKinds.Action or NodeKinds.ActionGroup or NodeKinds.Initialize or NodeKinds.Ref;

        if (node.Kind == NodeKinds.Ref)
            yield return Item(PipelineEditorMenuIds.CopyTemplateHere, "Copy for this pipeline only\u2026", NodeMenuGroup.Copy, CanEdit);
        if (isStep || node.Kind is NodeKinds.Event or NodeKinds.Template)
            yield return Item(PipelineEditorMenuIds.AddStep, isStep ? "Add step after" : "Add step", NodeMenuGroup.Add, CanEdit);
        if (node.Kind == NodeKinds.Action && !string.IsNullOrEmpty(agent))
            yield return Item(PipelineEditorMenuIds.AddRestartAfter, "Add restart after", NodeMenuGroup.Add, CanEdit);
        if (isStep)
            yield return Item(PipelineEditorMenuIds.AddWaitAfter, "Add wait after", NodeMenuGroup.Add, CanEdit);
        if (node.Kind is NodeKinds.Action or NodeKinds.ActionGroup or NodeKinds.Event && _agentStatuses is { Count: > 0 })
            yield return Item(PipelineEditorMenuIds.ChangeAgent, "Change agent\u2026", NodeMenuGroup.Edit, CanEdit);
        if (node.Kind == NodeKinds.Action && !string.IsNullOrEmpty(agent) && _agentStatuses is { Count: > 1 })
            yield return Item(PipelineEditorMenuIds.ApplyToAgents, "Apply to agents\u2026", NodeMenuGroup.Copy, CanEdit);
    }

    /// <summary>Agents for the Change agent / Apply to agents pickers, with their status.</summary>
    public IReadOnlyList<XmlEditorAgentChoice> AgentChoices =>
        (_agentStatuses ?? []).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new XmlEditorAgentChoice(a.Name, a.Status)).ToList();

    /// <summary>Handles a quick action; null when <paramref name="id"/> is not one.</summary>
    /// <param name="input">Change agent: the agent. Apply to agents: the agents, separated by ';'.</param>
    private bool? RunQuickAction(string id, PipelineStructureNode node, string? input)
    {
        PipelineXmlEditResult? result;
        switch (id)
        {
            case PipelineEditorMenuIds.AddStep:
                result = PipelineQuickActions.AddStep(XmlText, node.Path);
                break;
            case PipelineEditorMenuIds.AddRestartAfter:
                result = PipelineQuickActions.AddRestartAfter(XmlText, node.Path);
                break;
            case PipelineEditorMenuIds.AddWaitAfter:
                result = PipelineQuickActions.AddWaitAfter(XmlText, node.Path);
                break;
            case PipelineEditorMenuIds.ChangeAgent:
                result = PipelineQuickActions.ChangeAgent(XmlText, node.Path, input ?? "");
                break;
            case PipelineEditorMenuIds.ApplyToAgents:
                result = PipelineQuickActions.ApplyToAgents(XmlText, node.Path,
                    (input ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
            case PipelineEditorMenuIds.ReloadParameterFile:
                _fieldsPath = null;
                _tokenCacheXml = null;
                LoadProperties(node);
                StatusMessage = "Reloaded the parameter file.";
                StatusSeverity = "Info";
                return true;
            default:
                return null;
        }
        var label = SelectedNodeActions.Concat(EditorOnlyItems(node)).FirstOrDefault(i => i.Id == id)?.Label.TrimEnd('\u2026') ?? id;
        return ApplyEditResult(result, $"{node.Name}: {label}");
    }

    private NodeMenuItem Item(string id, string label, NodeMenuGroup group, bool enabled, string? tip = null) =>
        new(id, label, group) { IsEnabled = enabled, ToolTip = enabled ? tip : tip ?? ReadOnlyReason ?? "Read-only" };
}

/// <summary>An agent offered by the Change agent / Apply to agents pickers.</summary>
public sealed record XmlEditorAgentChoice(string Name, string Status)
{
    public string Display => $"{Name}  -  {Status}";
}
