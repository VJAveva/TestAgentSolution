using System.IO;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// PipelineEditor_Requirement.md Part 4 (section 5): Form view (step cards) and XML view of the same text.
public sealed partial class WatchItemXmlEditorViewModel
{
    /// <summary>Form view (step cards) is the default; the XML view is for advanced users. Both edit the same text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXmlView))]
    private bool _isFormView = true;

    public bool IsXmlView
    {
        get => !IsFormView;
        set => IsFormView = !value;
    }

    [ObservableProperty] private bool _wordWrap;

    /// <summary>Set while the XML is not well-formed: the form shows the last good state and cannot be edited.</summary>
    [ObservableProperty] private string _formUnavailableMessage = "";

    // Leaving the form for the XML: put the caret on the node the user was looking at.
    partial void OnIsFormViewChanged(bool value)
    {
        if (value)
        {
            RefreshStructure();
            return;
        }
        if (SelectedStructureNode is { } node && _map?.Find(node.Path.Split('|')[0]) is { } mapNode)
            NavigateToOffset?.Invoke(mapNode.Start);
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
