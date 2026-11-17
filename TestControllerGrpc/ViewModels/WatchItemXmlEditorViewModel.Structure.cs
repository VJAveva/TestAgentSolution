using System.Collections.ObjectModel;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>One row of the Pipeline Editor's structure tree.</summary>
public sealed partial class PipelineStructureNode : ObservableObject
{
    public PipelineStructureNode(PipelineXmlNode node)
    {
        Path = node.Path;
        Kind = node.Kind;
        Name = node.Name;
        Line = node.Line;
        SearchText = node.SearchText;
        IsSkipped = node.IsSkipped;
    }

    public string Path { get; }
    public string Kind { get; }
    public string Name { get; }
    public int Line { get; }
    public string SearchText { get; }
    public bool IsSkipped { get; }
    public PipelineStructureNode? Parent { get; set; }
    public ObservableCollection<PipelineStructureNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

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

/// <summary>A read-only attribute of the selected node; secret values are masked.</summary>
public sealed record PipelinePropertyRow(string Name, string Value);

// PipelineEditor_Requirement.md Part 1: structure tree, shared selection, problems on nodes, structure menu.
public sealed partial class WatchItemXmlEditorViewModel
{
    private PipelineXmlMap? _map;
    private bool _selectingFromCaret;

    public ObservableCollection<PipelineStructureNode> StructureRoots { get; } = new();
    public ObservableCollection<PipelinePropertyRow> SelectedProperties { get; } = new();

    [ObservableProperty] private PipelineStructureNode? _selectedStructureNode;
    [ObservableProperty] private string _structureFilter = "";
    [ObservableProperty] private string _selectedNodeTitle = "";

    /// <summary>False while the XML is not well-formed; the tree then shows its last good state.</summary>
    public bool IsStructureAvailable => _map is not null;

    /// <summary>Asks the XML view to put the caret at an offset (selection came from the tree or a problem).</summary>
    public event Action<int>? NavigateToOffset;

    /// <summary>Called by the XML view whenever the caret moves.</summary>
    public void SyncSelectionToCaret(int offset)
    {
        if (_map?.NodeAt(offset) is not { } node || node.Path == SelectedStructureNode?.Path) return;
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
        LoadProperties(newValue);
        if (!_selectingFromCaret && newValue is not null && _map?.Find(newValue.Path) is { } node)
            NavigateToOffset?.Invoke(node.Start);
    }

    partial void OnStructureFilterChanged(string value) => ApplyStructureFilter();

    private void RebuildStructure()
    {
        _map = PipelineXmlMap.TryBuild(XmlText);
        OnPropertyChanged(nameof(IsStructureAvailable));
        if (_map is null) return;

        var collapsed = Flatten(StructureRoots).Where(n => !n.IsExpanded).Select(n => n.Path).ToHashSet();
        var selected = SelectedStructureNode?.Path;

        StructureRoots.Clear();
        foreach (var root in _map.Roots)
            StructureRoots.Add(ToStructure(root, null, collapsed));

        ApplyProblemsToStructure();
        ApplyStructureFilter();

        _selectingFromCaret = true;
        try
        {
            SelectedStructureNode = null;
            if (selected is not null) SelectPath(selected);
        }
        finally { _selectingFromCaret = false; }
    }

    private static PipelineStructureNode ToStructure(PipelineXmlNode node, PipelineStructureNode? parent, HashSet<string> collapsed)
    {
        var row = new PipelineStructureNode(node) { Parent = parent, IsExpanded = !collapsed.Contains(node.Path) };
        foreach (var child in node.Children)
            row.Children.Add(ToStructure(child, row, collapsed));
        return row;
    }

    /// <summary>Puts each problem on the node it belongs to: a badge on the tree row, a node name in the list.</summary>
    private void ApplyProblemsToStructure()
    {
        var rows = Flatten(StructureRoots).ToDictionary(n => n.Path);
        foreach (var row in rows.Values)
        {
            row.ErrorCount = 0;
            row.WarningCount = 0;
        }
        foreach (var problem in ValidationErrors)
        {
            var node = _map?.NodeAt(problem.Line, problem.Column);
            problem.NodeLabel = node?.Name ?? "";
            problem.NodePath = node?.Path;
            if (node is null || !rows.TryGetValue(node.Path, out var row)) continue;
            if (problem.Severity == "Error") row.ErrorCount++;
            else row.WarningCount++;
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

    private void LoadProperties(PipelineStructureNode? node)
    {
        SelectedProperties.Clear();
        SelectedNodeTitle = node is null ? "" : $"{node.Kind}: {node.Name}";
        if (node is null || PipelineXmlEdits.TryParse(XmlText) is not { } document
            || PipelinePaths.Resolve(document, node.Path) is not { } element) return;

        foreach (var attribute in element.Attributes())
            SelectedProperties.Add(new PipelinePropertyRow(attribute.Name.LocalName, DisplayValue(attribute)));
        if (element.Element("Comment")?.Value is { Length: > 0 } note)
            SelectedProperties.Add(new PipelinePropertyRow("Comment", note));
    }

    /// <summary>Secret values are never shown: SecretEnv lists names only, secret-named attributes are masked.</summary>
    private static string DisplayValue(XAttribute attribute)
    {
        var name = attribute.Name.LocalName;
        if (name.Equals("SecretEnv", StringComparison.OrdinalIgnoreCase))
            return string.Join(", ", attribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('=', 2)[0].Trim())) + " (values hidden)";
        if (TokenDisplay.IsSecret(name)) return TokenDisplay.Mask;
        return SecurityRedactor.Redact(attribute.Value) ?? "";
    }

    /// <summary>The structure menu: the main tree's rules (NodeMenuProvider), limited to what this editor offers.</summary>
    public IReadOnlyList<NodeMenuItem> StructureMenuFor(PipelineStructureNode node)
    {
        if (PipelineXmlEdits.TryParse(XmlText) is not { } document
            || PipelinePaths.Resolve(document, node.Path) is not { } element) return [];

        var isRoot = node.Path.Length == 0;
        var context = new NodeMenuContext
        {
            NodeKind = node.Kind,
            IsSkippable = node.Kind is NodeKinds.Event or NodeKinds.ActionGroup or NodeKinds.Action,
            IsSkipped = node.IsSkipped,
            ExecutionType = element.Attribute("ExecutionType")?.Value ?? "Sequential",
            CanMoveUp = !isRoot && PipelineXmlEdits.CanMove(element, up: true),
            CanMoveDown = !isRoot && PipelineXmlEdits.CanMove(element, up: false),
        };
        return NodeMenuProvider.Build(context)
            .Where(item => NodeMenuProvider.PipelineEditorIds.Contains(item.Id))
            .Where(item => !(isRoot && item.Id == NodeMenuIds.Delete))
            .Select(item => item with
            {
                Children = item.Children.Where(c => NodeMenuProvider.PipelineEditorIds.Contains(c.Id)).ToList(),
            })
            .ToList();
    }

    /// <summary>Runs a structure menu item on the XML. One editor undo step; the changed node stays selected.</summary>
    public bool RunStructureCommand(string id, PipelineStructureNode node, string? skipReason = null)
    {
        var result = id switch
        {
            NodeMenuIds.Delete => PipelineXmlEdits.Delete(XmlText, node.Path),
            NodeMenuIds.MoveUp => PipelineXmlEdits.Move(XmlText, node.Path, up: true),
            NodeMenuIds.MoveDown => PipelineXmlEdits.Move(XmlText, node.Path, up: false),
            NodeMenuIds.Duplicate => PipelineXmlEdits.Duplicate(XmlText, node.Path),
            NodeMenuIds.Skip => PipelineXmlEdits.SetSkip(XmlText, node.Path, true, skipReason, Environment.UserName, DateTimeOffset.UtcNow),
            NodeMenuIds.Include => PipelineXmlEdits.SetSkip(XmlText, node.Path, false, null, null, DateTimeOffset.UtcNow),
            NodeMenuIds.ExecutionSequential => PipelineXmlEdits.SetExecutionType(XmlText, node.Path, ExecutionMode.Sequential),
            NodeMenuIds.ExecutionParallel => PipelineXmlEdits.SetExecutionType(XmlText, node.Path, ExecutionMode.Parallel),
            _ => null,
        };
        if (result is null)
        {
            StatusMessage = "Nothing to change.";
            StatusSeverity = "Info";
            return false;
        }

        XmlText = result.Xml;
        SelectPath(result.SelectPath);
        StatusMessage = $"{node.Name}: {id} - Ctrl+Z to undo.";
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
