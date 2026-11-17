using System.Collections.ObjectModel;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

public enum EditorPaneView { Form, Xml, Parameters }

// PipelineEditor_Requirement.md Part 4 (section 5): Form view (step cards) and XML view of the same text.
public sealed partial class WatchItemXmlEditorViewModel
{
    /// <summary>Form view (step cards) is the default; XML is for advanced users; Parameters is read-only. One text behind all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormView), nameof(IsXmlView), nameof(IsParametersView))]
    private EditorPaneView _editorView = EditorPaneView.Form;

    public bool IsFormView
    {
        get => EditorView == EditorPaneView.Form;
        set { if (value) EditorView = EditorPaneView.Form; }
    }

    public bool IsXmlView
    {
        get => EditorView == EditorPaneView.Xml;
        set { if (value) EditorView = EditorPaneView.Xml; }
    }

    public bool IsParametersView
    {
        get => EditorView == EditorPaneView.Parameters;
        set { if (value) EditorView = EditorPaneView.Parameters; }
    }

    [ObservableProperty] private bool _wordWrap;

    /// <summary>Set while the XML is not well-formed: the form shows the last good state and cannot be edited.</summary>
    [ObservableProperty] private string _formUnavailableMessage = "";

    public ObservableCollection<PipelineParameterRow> ParameterRows { get; } = new();
    [ObservableProperty] private string _parametersMessage = "";

    /// <summary>The pipeline's first Initialize parameter file, for "Open parameter file"; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParameterFile))]
    private string? _parameterFilePath;

    public bool HasParameterFile => !string.IsNullOrWhiteSpace(ParameterFilePath);

    partial void OnEditorViewChanged(EditorPaneView value)
    {
        switch (value)
        {
            case EditorPaneView.Form:
                RefreshStructure();
                break;
            case EditorPaneView.Xml:
                // Leaving the form for the XML: put the caret on the node the user was looking at.
                if (SelectedStructureNode is { } node && _map?.Find(node.Path.Split('|')[0]) is { } mapNode)
                    NavigateToOffset?.Invoke(mapNode.Start);
                break;
            case EditorPaneView.Parameters:
                LoadParameters();
                break;
        }
    }

    /// <summary>Fills the Parameters view for this tab's pipeline (or, in the whole WatchList, the selected one's).</summary>
    public void LoadParameters()
    {
        ParameterRows.Clear();
        ParameterFilePath = null;
        WatchItemConfig? item = null;
        try
        {
            item = _documentKind switch
            {
                XmlEditorDocumentKind.WatchItem => WatchListXmlParser.DeserializeWatchItem(XmlText),
                XmlEditorDocumentKind.WatchList when SelectedStructureNode?.Path is { } path
                    && WatchListXmlParser.DeserializeWatchList(XmlText) is { } list
                    && int.TryParse(path.Split('/')[0].TrimStart('w'), out var index) && index < list.WatchItems.Count
                    => list.WatchItems[index],
                _ => null,
            };
        }
        catch (XmlException) { }

        if (item is null)
        {
            ParametersMessage = _documentKind switch
            {
                XmlEditorDocumentKind.Templates => "Parameters belong to a pipeline; a template has none of its own.",
                XmlEditorDocumentKind.WatchList => "Select a pipeline in the tree to see its parameters.",
                _ => FormUnavailableMessage.Length > 0 ? FormUnavailableMessage : "The XML cannot be read.",
            };
            return;
        }
        foreach (var row in PipelineParameterTable.Build(item, ValidationContext))
            ParameterRows.Add(row);
        ParameterFilePath = ParameterResolver.CollectInitializeNodes(item).Select(n => n.ParameterFile)
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
        var unresolved = ParameterRows.Count(r => r.IsUnresolved);
        ParametersMessage = $"{ParameterRows.Count} token(s) for '{item.Tag}'"
            + (unresolved > 0 ? $"; {unresolved} used but not defined" : "")
            + ". Values are read-only here - change them in the parameter file or the Initialize grid.";
    }

    private void ShowXmlNotWellFormed()
    {
        var line = 0;
        try { XDocument.Parse(XmlText); }
        catch (XmlException ex) { line = ex.LineNumber; }
        FormUnavailableMessage = line > 0
            ? $"Fix the XML error on line {line} to use the form."
            : "Fix the XML error to use the form.";
        foreach (var field in SelectedFields)
            field.ReadOnlyReason = FormUnavailableMessage;
    }

    /// <summary>The second line of a step card. Values come from the node's own attributes; credentials are redacted.</summary>
    private static string StepSummary(string text, PipelineXmlNode node)
    {
        var attributes = PipelineXmlText.Attributes(text, node.Start)
            .GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Value);
        string? A(string name) => attributes.TryGetValue(name, out var v) && v.Length > 0 ? v : null;

        string?[] parts = node.Kind switch
        {
            NodeKinds.Action when A("Type") == nameof(ActionType.SendMail) => [$"Mail to {A("To")}", A("Title")],
            NodeKinds.Action => [A("AgentName"), A("Command"), A("Timeout") is { } t && t != "0" ? $"timeout {t}s" : null],
            NodeKinds.ActionGroup => [A("ExecutionType") ?? nameof(ExecutionMode.Sequential),
                A("FailAndContinue") == "true" ? "continue on failure" : null],
            NodeKinds.Initialize => [Path.GetFileName(A("ParameterFile") ?? ""), A("Profile") is { } p ? $"profile {p}" : null],
            NodeKinds.Ref => [A("TemplateID") is { } id ? $"template {id}" : null],
            NodeKinds.WatchItem => [A("Path") is { } path ? Path.Combine(path, A("Filter") ?? "*.*") : null],
            _ => [],
        };
        return SecurityRedactor.Redact(string.Join("   |   ", parts.Where(p => !string.IsNullOrWhiteSpace(p)))) ?? "";
    }
}
