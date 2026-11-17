using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;

namespace TestControllerGrpc.ViewModels;

/// <summary>One row of the Pipeline Editor's structure tree.</summary>
public sealed partial class PipelineStructureNode : ObservableObject
{
    /// <param name="templateId">Set for a template's step shown under a Ref (read-only, "from template").</param>
    /// <param name="path">Tree path when it differs from the node's own path (template steps).</param>
    public PipelineStructureNode(PipelineXmlNode node, string? agentStatus, string? templateId = null, string? path = null)
    {
        Path = path ?? node.Path;
        Kind = node.Kind;
        Name = node.Name;
        Line = node.Line;
        SearchText = node.SearchText;
        IsSkipped = node.IsSkipped;
        AgentStatus = agentStatus;
        TemplateId = templateId;
        TemplatePath = templateId is null ? null : node.Path;
    }

    public string Path { get; }
    public string Kind { get; }
    public string Name { get; }
    public int Line { get; }
    public string SearchText { get; }
    public bool IsSkipped { get; }

    /// <summary>Template steps only: the template they belong to and their path inside its document.</summary>
    public string? TemplateId { get; }
    public string? TemplatePath { get; }
    public bool IsFromTemplate => TemplatePath is not null;

    /// <summary>One line for the Form view's step card: agent, command, timeout and similar (credentials redacted).</summary>
    public string Summary { get; init; } = "";
    public bool HasSummary => Summary.Length > 0;

    /// <summary>Status of the step's agent when it cannot take work now (offline, not registered...); null otherwise.</summary>
    public string? AgentStatus { get; }
    public bool IsAgentUnavailable => AgentStatus is not null;

    public PipelineStructureNode? Parent { get; set; }
    public ObservableCollection<PipelineStructureNode> Children { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorCount), nameof(WarningCount))]
    private bool _isExpanded = true;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorCount))] private int _ownErrorCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WarningCount))] private int _ownWarningCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorCount))] private int _subtreeErrorCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WarningCount))] private int _subtreeWarningCount;

    /// <summary>The node's own count; a collapsed node also counts what is hidden under it.</summary>
    public int ErrorCount => IsExpanded ? OwnErrorCount : SubtreeErrorCount;
    public int WarningCount => IsExpanded ? OwnWarningCount : SubtreeWarningCount;

    public string KindBadge => Kind switch
    {
        NodeKinds.WatchItem => "PIPE",
        NodeKinds.Event => "EVT",
        NodeKinds.ActionGroup => "GRP",
        NodeKinds.Action => "ACT",
        NodeKinds.Initialize => "INIT",
        NodeKinds.Ref => "REF",
        NodeKinds.Template => "TPL",
        _ => "?",
    };
}

// PipelineEditor_Requirement.md Part 1: structure tree, shared selection, problems on nodes, structure menu.
public sealed partial class WatchItemXmlEditorViewModel
{
    private PipelineXmlMap? _map;
    private bool _structureDirty = true;
    private bool _selectingFromCaret;

    public ObservableCollection<PipelineStructureNode> StructureRoots { get; } = new();

    [ObservableProperty] private PipelineStructureNode? _selectedStructureNode;
    [ObservableProperty] private string _structureFilter = "";
    [ObservableProperty] private string _selectedNodeTitle = "";

    /// <summary>False while the XML is not well-formed; the tree then shows its last good state.</summary>
    public bool IsStructureAvailable => _map is not null;

    /// <summary>Asks the XML view to put the caret at an offset (selection came from the tree or a problem).</summary>
    public event Action<int>? NavigateToOffset;

    /// <summary>Rebuilds the tree if the text changed since the last build. The view calls it after typing pauses.</summary>
    public void RefreshStructure()
    {
        if (_structureDirty) RebuildStructure();
    }

    /// <summary>Called by the XML view whenever the caret moves.</summary>
    public void SyncSelectionToCaret(int offset)
    {
        if (_structureDirty || _map?.NodeAt(offset) is not { } node || node.Path == SelectedStructureNode?.Path) return;
        _selectingFromCaret = true;
        try { SelectPath(node.Path); }
        finally { _selectingFromCaret = false; }
    }

    public bool SelectPath(string path)
    {
        var target = Flatten(StructureRoots).FirstOrDefault(n => n.Path == path);
        if (target is null) return false;
        for (var parent = target.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        SelectedStructureNode = target;
        return true;
    }

    partial void OnSelectedStructureNodeChanged(PipelineStructureNode? oldValue, PipelineStructureNode? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (_structureDirty) return;
        LoadProperties(newValue);
        if (!_selectingFromCaret && newValue is not null && _map?.Find(newValue.Path.Split('|')[0]) is { } node)
            NavigateToOffset?.Invoke(node.Start);
    }

    partial void OnStructureFilterChanged(string value) => ApplyStructureFilter();

    private void RebuildStructure()
    {
        _structureDirty = false;
        _map = PipelineXmlMap.TryBuild(XmlText);
        OnPropertyChanged(nameof(IsStructureAvailable));
        if (_map is null)
        {
            ShowXmlNotWellFormed();
            return;
        }
        FormUnavailableMessage = "";

        var collapsed = Flatten(StructureRoots).Where(n => !n.IsExpanded).Select(n => n.Path).ToHashSet();
        var expandedCalls = Flatten(StructureRoots).Where(n => n.IsExpanded && n.Kind == NodeKinds.Ref).Select(n => n.Path).ToHashSet();
        var selected = SelectedStructureNode?.Path;

        StructureRoots.Clear();
        foreach (var root in _map.Roots)
            StructureRoots.Add(ToStructure(root, null, collapsed, expandedCalls));

        ApplyStructureFilter();

        // Reselect by path without moving the caret; the same node keeps its form (fields update in place).
        _selectingFromCaret = true;
        try
        {
            var again = selected is null ? null : Flatten(StructureRoots).FirstOrDefault(n => n.Path == selected);
            if (again is null || !SelectPath(again.Path)) SelectedStructureNode = null;
        }
        finally { _selectingFromCaret = false; }

        ApplyProblemsToStructure();
    }

    // A template call starts collapsed: its steps live in the template, not in this pipeline.
    private PipelineStructureNode ToStructure(
        PipelineXmlNode node, PipelineStructureNode? parent, HashSet<string> collapsed, HashSet<string> expandedCalls)
    {
        var row = new PipelineStructureNode(node, UnavailableAgentStatus(node.Agent))
        {
            Parent = parent,
            IsExpanded = node.Kind == NodeKinds.Ref ? expandedCalls.Contains(node.Path) : !collapsed.Contains(node.Path),
            Summary = StepSummary(XmlText, node),
        };
        foreach (var child in node.Children)
            row.Children.Add(ToStructure(child, row, collapsed, expandedCalls));
        if (node.Kind == NodeKinds.Ref)
            AddTemplateSteps(row, TemplateIdOf(row));
        return row;
    }

    // Agent names that are tokens resolve at run time, so they get no badge here.
    private string? UnavailableAgentStatus(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent) || agent.StartsWith('[') || _agentStatuses is null) return null;
        var status = _agentStatuses.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase))?.Status;
        return status switch
        {
            null => "Not registered",
            "Online" or "Busy" => null,
            _ => status,
        };
    }

    /// <summary>Puts each problem on the node it belongs to: a badge on the tree row, a node name in the list.</summary>
    private void ApplyProblemsToStructure()
    {
        var rows = Flatten(StructureRoots).ToDictionary(n => n.Path);
        var errors = rows.Keys.ToDictionary(p => p, _ => 0);
        var warnings = rows.Keys.ToDictionary(p => p, _ => 0);
        foreach (var problem in ValidationErrors)
        {
            var node = _map?.NodeAt(problem.Line, problem.Column);
            problem.NodeLabel = node?.Name ?? "";
            problem.NodePath = node?.Path;
            if (node is null || !rows.ContainsKey(node.Path)) continue;
            if (problem.Severity == "Error") errors[node.Path]++;
            else warnings[node.Path]++;
        }

        foreach (var root in StructureRoots) Total(root);
        UpdateFieldErrors();

        (int Errors, int Warnings) Total(PipelineStructureNode node)
        {
            node.OwnErrorCount = errors[node.Path];
            node.OwnWarningCount = warnings[node.Path];
            var (e, w) = (node.OwnErrorCount, node.OwnWarningCount);
            foreach (var child in node.Children)
            {
                var (ce, cw) = Total(child);
                e += ce;
                w += cw;
            }
            node.SubtreeErrorCount = e;
            node.SubtreeWarningCount = w;
            return (e, w);
        }
    }

    private void ApplyStructureFilter()
    {
        var filter = StructureFilter.Trim();
        foreach (var root in StructureRoots) Apply(root);

        bool Apply(PipelineStructureNode node)
        {
            var childVisible = false;
            foreach (var child in node.Children) childVisible |= Apply(child);
            var matches = filter.Length == 0
                || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || node.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase);
            node.IsVisible = matches || childVisible;
            if (filter.Length > 0 && childVisible) node.IsExpanded = true;
            return node.IsVisible;
        }
    }

    /// <summary>The structure menu: the main tree's rules (NodeMenuProvider), limited to what this editor offers.</summary>
    public IReadOnlyList<NodeMenuItem> StructureMenuFor(PipelineStructureNode node)
    {
        RefreshStructure();
        var isRoot = node.Path.Length == 0;
        string? executionType = null;
        if (!node.IsFromTemplate)
        {
            if (_map?.Find(node.Path) is not { } mapNode) return [];
            executionType = PipelineXmlText.Attributes(XmlText, mapNode.Start)
                .FirstOrDefault(a => a.Name == "ExecutionType")?.Value;
        }

        var templateId = node.Kind == NodeKinds.Ref || node.IsFromTemplate ? TemplateIdOf(node) : null;
        var templateBlocked = TemplateEditBlocked?.Invoke();
        var context = new NodeMenuContext
        {
            NodeKind = node.Kind,
            InTemplateCall = node.Kind == NodeKinds.Ref || node.IsFromTemplate,
            IsTemplateCallRoot = node.Kind == NodeKinds.Ref && !node.IsFromTemplate,
            CalledTemplateId = templateId,
            CalledTemplateExists = TemplateDocumentFor(templateId) is not null,
            TemplateEditBlockedReason = templateBlocked,
            IsSkippable = node.Kind is NodeKinds.Event or NodeKinds.ActionGroup or NodeKinds.Action,
            IsSkipped = node.IsSkipped,
            ExecutionType = executionType ?? nameof(ExecutionMode.Sequential),
            CanMoveUp = !isRoot && !node.IsFromTemplate && _map is not null && PipelineXmlEdits.CanMove(_map, node.Path, up: true),
            CanMoveDown = !isRoot && !node.IsFromTemplate && _map is not null && PipelineXmlEdits.CanMove(_map, node.Path, up: false),
            EditBlockedReason = ReadOnlyReason,
        };
        return NodeMenuProvider.Build(context)
            .Where(item => NodeMenuProvider.PipelineEditorIds.Contains(item.Id))
            .Where(item => !(isRoot && item.Id == NodeMenuIds.Delete))
            .Where(item => !(node.Kind == NodeKinds.Template && item.Id == NodeMenuIds.Delete))
            .Where(item => !(node.IsFromTemplate && item.Id != NodeMenuIds.EditTemplate))
            .Select(item => item with
            {
                Children = item.Children.Where(c => NodeMenuProvider.PipelineEditorIds.Contains(c.Id)).ToList(),
            })
            .Concat(EditorOnlyItems(node))
            .ToList();
    }

    /// <summary>Why templates cannot be changed (no Template_Edit); null when they can. Set by the host.</summary>
    public Func<string?>? TemplateEditBlocked { get; set; }

    /// <summary>Runs a structure menu item on the XML. One editor undo step; the changed node stays selected.</summary>
    /// <param name="input">The skip reason (Skip) or the new template's name (Save group as template).</param>
    public bool RunStructureCommand(string id, PipelineStructureNode node, string? input = null)
    {
        if (id == NodeMenuIds.EditTemplate)
        {
            if (TemplateIdOf(node) is not { } templateId) return false;
            EditTemplate(templateId, node.TemplatePath);
            return true;
        }
        if (IsReadOnly || node.IsFromTemplate)
        {
            StatusMessage = ReadOnlyReason ?? TemplateBlockedReason(node);
            StatusSeverity = "Error";
            return false;
        }

        if (id == PipelineEditorMenuIds.CopyTemplateHere) return CopyTemplateHere(node);
        if (id == NodeMenuIds.SaveAsTemplate) return SaveGroupAsTemplate(node, input);

        var result = id switch
        {
            NodeMenuIds.Delete or NodeMenuIds.DeleteTemplateCall => PipelineXmlEdits.Delete(XmlText, node.Path),
            NodeMenuIds.MoveUp => PipelineXmlEdits.Move(XmlText, node.Path, up: true),
            NodeMenuIds.MoveDown => PipelineXmlEdits.Move(XmlText, node.Path, up: false),
            NodeMenuIds.Duplicate => PipelineXmlEdits.Duplicate(XmlText, node.Path),
            NodeMenuIds.Skip => PipelineXmlEdits.SetSkip(XmlText, node.Path, true, input, Environment.UserName, DateTimeOffset.UtcNow),
            NodeMenuIds.Include => PipelineXmlEdits.SetSkip(XmlText, node.Path, false, null, null, DateTimeOffset.UtcNow),
            NodeMenuIds.ExecutionSequential => PipelineXmlEdits.SetExecutionType(XmlText, node.Path, ExecutionMode.Sequential),
            NodeMenuIds.ExecutionParallel => PipelineXmlEdits.SetExecutionType(XmlText, node.Path, ExecutionMode.Parallel),
            _ => null,
        };
        return ApplyEditResult(result, $"{node.Name}: {id}");
    }

    private bool ApplyEditResult(PipelineXmlEditResult? result, string what)
    {
        if (result is null)
        {
            StatusMessage = "Nothing to change.";
            StatusSeverity = "Info";
            return false;
        }

        XmlText = result.Xml;
        RebuildStructure();
        if (SelectedStructureNode?.Path != result.SelectPath) SelectPath(result.SelectPath);
        StatusMessage = $"{what} - Ctrl+Z to undo.";
        StatusSeverity = "Info";
        return true;
    }

    private static IEnumerable<PipelineStructureNode> Flatten(IEnumerable<PipelineStructureNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
