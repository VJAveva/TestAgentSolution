using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>Menu ids that exist only in the Pipeline Editor (the main tree has no equivalent).</summary>
public static class PipelineEditorMenuIds
{
    public const string CopyTemplateHere = "CopyTemplateHere";
}

// PipelineEditor_Requirement.md Part 3 (section 4): templates on this screen.
public sealed partial class WatchItemXmlEditorViewModel
{
    [ObservableProperty] private string _tabTitle = "Pipeline";
    [ObservableProperty] private string _banner = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(CanEdit))]
    private string? _readOnlyReason;

    public bool IsReadOnly => ReadOnlyReason is not null;
    public bool CanEdit => ReadOnlyReason is null;

    /// <summary>Template tabs: the ID the template had when the tab opened; null for a template created here.</summary>
    public string? TemplateOriginalId { get; init; }
    public bool IsTemplateTab => _documentKind == XmlEditorDocumentKind.Templates;
    public string OriginalXml => _originalXml;

    /// <summary>The current text of a template's document (an open tab's edits win over the Library); null when unknown.</summary>
    public Func<string, string?>? TemplateSource { get; set; }

    /// <summary>Asks the host to open (or activate) the template's tab with the given step selected.</summary>
    public event Action<string, string?>? EditTemplateRequested;

    /// <summary>A template made from a group here: its ID and document. The host opens it as a new tab.</summary>
    public event Action<string, string>? TemplateCreated;

    /// <summary>Replaces the text and the baseline with the latest saved version (Reload after a conflict).</summary>
    public void Reload(string latestXml)
    {
        _originalXml = latestXml;
        XmlText = latestXml;
        RebuildStructure();
        StatusMessage = "Reloaded the latest saved version.";
        StatusSeverity = "Info";
    }

    /// <summary>Shows save-blocking problems in pipelines that use the edited template.</summary>
    public void ShowImpactProblems(IReadOnlyList<TemplateImpactProblem> problems)
    {
        foreach (var problem in problems)
            ValidationErrors.Add(new ValidationErrorViewModel
            {
                Line = 1, Column = 1, Severity = "Error", ErrorType = "Impact",
                Message = $"Pipeline '{problem.Pipeline}': {problem.Message}", NodeLabel = problem.Pipeline,
            });
        IsErrorPanelVisible = ValidationErrors.Count > 0;
        StatusMessage = $"Save blocked: this change breaks {problems.Select(p => p.Pipeline).Distinct().Count()} pipeline(s) that use the template.";
        StatusSeverity = "Error";
    }

    private string? TemplateDocumentFor(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return null;
        if (TemplateSource?.Invoke(templateId) is { } open) return open;
        var template = ValidationContext?.Templates.FirstOrDefault(t => string.Equals(t.ID, templateId, StringComparison.OrdinalIgnoreCase));
        return template is null ? null : PipelineTemplateEdits.TemplateDocument(template);
    }

    /// <summary>In a whole-WatchList document the template is in the same text, so Edit template just selects it.</summary>
    private void EditTemplate(string templateId, string? templatePath)
    {
        if (_documentKind == XmlEditorDocumentKind.WatchList
            && Flatten(StructureRoots).FirstOrDefault(n => n.Kind == NodeKinds.Template
                && string.Equals(n.Name, templateId, StringComparison.OrdinalIgnoreCase)) is { } inDocument)
        {
            var inner = templatePath is { Length: > 2 } ? templatePath[templatePath.IndexOf('/')..] : "";
            if (!SelectPath(inDocument.Path + inner)) SelectPath(inDocument.Path);
            return;
        }
        EditTemplateRequested?.Invoke(templateId, templatePath);
    }

    private bool CopyTemplateHere(PipelineStructureNode call)
    {
        var templateId = TemplateIdOf(call);
        if (TemplateDocumentFor(templateId) is not { } document)
        {
            StatusMessage = $"Template '{templateId}' is not in the Library.";
            StatusSeverity = "Error";
            return false;
        }
        return ApplyEditResult(PipelineTemplateEdits.InlineTemplate(XmlText, call.Path, document),
            $"Copied template '{templateId}' into this pipeline");
    }

    private bool SaveGroupAsTemplate(PipelineStructureNode group, string? templateId)
    {
        templateId = templateId?.Trim();
        if (string.IsNullOrEmpty(templateId)) return false;
        if (TemplateDocumentFor(templateId) is not null)
        {
            StatusMessage = $"A template named '{templateId}' already exists.";
            StatusSeverity = "Error";
            return false;
        }
        if (PipelineTemplateEdits.GroupToTemplate(XmlText, group.Path, templateId) is not { } made) return false;
        if (!ApplyEditResult(made.Pipeline, $"Saved group '{group.Name}' as template '{templateId}'")) return false;
        TemplateCreated?.Invoke(templateId, made.TemplateDocument);
        return true;
    }

    private string? TemplateIdOf(PipelineStructureNode node) =>
        node.TemplateId ?? (_map?.Find(node.Path) is { } mapNode
            ? PipelineXmlText.Attributes(XmlText, mapNode.Start).FirstOrDefault(a => a.Name == "TemplateID")?.Value
            : null);

    /// <summary>The template's steps under a Ref: read-only rows marked "from template".</summary>
    private void AddTemplateSteps(PipelineStructureNode call, string? templateId)
    {
        if (TemplateDocumentFor(templateId) is not { } document
            || PipelineXmlMap.TryBuild(document) is not { Roots: [{ Kind: NodeKinds.Template } template, ..] })
            return;
        foreach (var step in template.Children)
            call.Children.Add(TemplateRow(step, call, templateId!, call.Path, document));
    }

    private static PipelineStructureNode TemplateRow(
        PipelineXmlNode node, PipelineStructureNode parent, string templateId, string refPath, string document)
    {
        var row = new PipelineStructureNode(node, null, templateId, $"{refPath}|{node.Path}")
        {
            Parent = parent,
            Summary = StepSummary(document, node),
        };
        foreach (var child in node.Children)
            row.Children.Add(TemplateRow(child, row, templateId, refPath, document));
        return row;
    }

    private string TemplateBlockedReason(PipelineStructureNode node) =>
        $"Step of template '{node.TemplateId}'. Use Edit template to change it.";

    private IEnumerable<NodeMenuItem> EditorOnlyItems(PipelineStructureNode node) =>
        node.Kind == NodeKinds.Ref && !node.IsFromTemplate
            ? [new NodeMenuItem(PipelineEditorMenuIds.CopyTemplateHere, "Copy for this pipeline only\u2026", NodeMenuGroup.Copy)
                with { IsEnabled = CanEdit, ToolTip = ReadOnlyReason }]
            : [];
}
