using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

public sealed record PipelineChoice(string Value, string Description)
{
    public string Display => Description.Length == 0 ? Value : $"{Value}  -  {Description}";
}

/// <summary>A <c>[_Token]</c> used in a field: its current value and where it comes from (secrets masked).</summary>
public sealed record PipelineTokenHint(string Token, string Description, bool IsResolved);

/// <summary>One field of the Properties form. Setting <see cref="Value"/> writes the attribute into the XML.</summary>
public sealed partial class PipelineFieldViewModel : ObservableObject
{
    private readonly Action<PipelineFieldViewModel> _commit;
    private bool _loading;

    public PipelineFieldViewModel(PipelineFieldSpec spec, Action<PipelineFieldViewModel> commit)
    {
        Spec = spec;
        _commit = commit;
    }

    public PipelineFieldSpec Spec { get; }
    public string Attribute => Spec.Attribute;
    public string Label => Spec.Label;
    public PipelineFieldKind Kind => Spec.Kind;
    public string? Hint => Spec.Hint;

    public bool IsBool => Kind == PipelineFieldKind.Bool;
    public bool IsChoice => Kind == PipelineFieldKind.Choice && !IsReadOnly;
    public bool IsMultiLine => Kind == PipelineFieldKind.MultiLine;
    public bool IsTextBox => !IsBool && !IsChoice;
    public bool CanBrowse => Kind is PipelineFieldKind.File or PipelineFieldKind.Folder && !IsReadOnly;

    public ObservableCollection<PipelineChoice> Choices { get; } = new();
    public ObservableCollection<PipelineTokenHint> Tokens { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BoolValue))] private string? _value = "";
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(IsChoice), nameof(IsTextBox), nameof(CanBrowse))]
    private string? _readOnlyReason;

    public bool IsReadOnly => ReadOnlyReason is not null;

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var flag) && flag;
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string? value)
    {
        // A ComboBox pushes null while its items are replaced; that is not the user clearing the field.
        if (_loading || IsReadOnly || value is null) return;
        _commit(this);
    }

    internal void Load(string value, string? readOnlyReason)
    {
        _loading = true;
        try
        {
            ReadOnlyReason = readOnlyReason;
            if (IsChoice && value.Length > 0 && Choices.All(c => c.Value != value))
                Choices.Add(new PipelineChoice(value, "not in the list"));
            Value = value;
        }
        finally { _loading = false; }
    }
}

// PipelineEditor_Requirement.md Part 2: Properties forms. Every field edit is one attribute splice in the XML.
public sealed partial class WatchItemXmlEditorViewModel
{
    private static readonly Regex TokenPattern = new(@"\[_\w+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private string? _fieldsPath;
    private string? _tokenCacheXml;
    private Dictionary<string, string> _tokenDescriptions = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<PipelineFieldViewModel> SelectedFields { get; } = new();

    /// <summary>Problems of the selected node that do not belong to one field.</summary>
    public ObservableCollection<string> SelectedNodeProblems { get; } = new();

    [ObservableProperty] private string _selectedNodeInfo = "";
    [ObservableProperty] private string _selectedNodeNote = "";

    private void LoadProperties(PipelineStructureNode? node)
    {
        SelectedNodeTitle = node is null ? "" : $"{node.Kind}: {node.Name}" + (node.IsFromTemplate ? " (from template)" : "");

        // A template step shown under a Ref is read from the template's own text.
        var source = node?.IsFromTemplate == true ? TemplateDocumentFor(node.TemplateId) : XmlText;
        var sourceMap = node?.IsFromTemplate == true && source is not null ? PipelineXmlMap.TryBuild(source) : _map;
        if (node is null || source is null || sourceMap?.Find(node.TemplatePath ?? node.Path) is not { } mapNode)
        {
            _fieldsPath = null;
            SelectedFields.Clear();
            SelectedNodeProblems.Clear();
            SelectedNodeInfo = "";
            SelectedNodeNote = "";
            return;
        }

        var attributes = PipelineXmlText.Attributes(source, mapNode.Start)
            .GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Value);
        var specs = PipelineNodeForms.For(node.Kind, name => attributes.GetValueOrDefault(name));

        // Same node, same fields: update values in place so the control being edited keeps focus.
        if (_fieldsPath != node.Path || !SelectedFields.Select(f => f.Spec).SequenceEqual(specs))
        {
            SelectedFields.Clear();
            foreach (var spec in specs)
            {
                var field = new PipelineFieldViewModel(spec, CommitField);
                foreach (var choice in ChoicesFor(spec, node.Kind))
                    field.Choices.Add(choice);
                SelectedFields.Add(field);
            }
        }
        _fieldsPath = node.Path;

        foreach (var field in SelectedFields)
        {
            var stored = attributes.GetValueOrDefault(field.Attribute);
            var shown = PipelineNodeForms.DisplayValue(field.Spec, stored);
            var redacted = SecurityRedactor.Redact(shown) ?? "";
            var reason = field.Spec.ReadOnlyReason
                ?? (redacted != shown ? "Contains a credential - not shown. Edit it in the XML or move it to SecretEnv." : null)
                ?? (node.IsFromTemplate ? TemplateBlockedReason(node) : ReadOnlyReason);
            field.Load(redacted, reason);
            LoadTokens(field, redacted);
        }

        SelectedNodeInfo = node.Kind == NodeKinds.Ref ? UsageText(attributes.GetValueOrDefault("TemplateID"))
            : node.IsFromTemplate ? TemplateBlockedReason(node) : "";
        SelectedNodeNote = PipelineXmlEdits.TryParse(source) is { } document
            && PipelinePaths.Resolve(document, node.TemplatePath ?? node.Path)?.Element("Comment")?.Value is { Length: > 0 } note
                ? note
                : "";
        UpdateFieldErrors();
    }

    private void CommitField(PipelineFieldViewModel field)
    {
        if (IsReadOnly || _fieldsPath is not { } path || SelectedStructureNode is not { IsFromTemplate: false } node) return;

        var value = field.IsMultiLine ? field.Value?.Replace("\r\n", "\n", StringComparison.Ordinal) : field.Value;
        var result = field.Attribute == "Skip"
            ? PipelineXmlEdits.SetSkip(XmlText, path, field.BoolValue,
                SelectedFields.FirstOrDefault(f => f.Attribute == "SkipReason")?.Value,
                Environment.UserName, DateTimeOffset.UtcNow)
            : PipelineXmlEdits.SetAttribute(XmlText, path, field.Attribute, value);
        if (result is null) return;
        ApplyEditResult(result, $"{node.Name}: {field.Label}");
    }

    private IEnumerable<PipelineChoice> ChoicesFor(PipelineFieldSpec spec, string kind) =>
        spec.Kind != PipelineFieldKind.Choice
            ? []
            : GetValueCompletions(spec.Attribute, insideEvent: kind == NodeKinds.Event)
                .Select(c => new PipelineChoice(c.Text, c.Description.Replace("Agent status: ", "", StringComparison.Ordinal)));

    private string UsageText(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId) || ValidationContext is null) return "";
        var users = TemplateUsage.PipelinesReferencing(ValidationContext, templateId);
        return $"Template '{templateId}' is used by {users.Count} pipeline{(users.Count == 1 ? "" : "s")}"
               + (users.Count == 0 ? "." : $": {string.Join(", ", users)}.");
    }

    private void LoadTokens(PipelineFieldViewModel field, string value)
    {
        field.Tokens.Clear();
        var tokens = TokenPattern.Matches(value).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tokens.Count == 0) return;

        if (_tokenCacheXml != XmlText)
        {
            _tokenDescriptions = GetTokenCompletions()
                .GroupBy(c => c.Text, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);
            _tokenCacheXml = XmlText;
        }
        foreach (var token in tokens)
        {
            var found = _tokenDescriptions.TryGetValue(token, out var description);
            field.Tokens.Add(new PipelineTokenHint(token,
                found ? description! : "Not defined in the loaded parameter layers", found));
        }
    }

    /// <summary>Shows each problem of the selected node under the field it is about; the rest above the form.</summary>
    private void UpdateFieldErrors()
    {
        SelectedNodeProblems.Clear();
        foreach (var field in SelectedFields) field.Error = null;
        if (_fieldsPath is null || _map?.Find(_fieldsPath) is not { } node) return;

        var attributes = PipelineXmlText.Attributes(XmlText, node.Start);
        foreach (var problem in ValidationErrors.Where(p => p.NodePath == _fieldsPath))
        {
            var offset = _map.OffsetOf(problem.Line, problem.Column);
            var name = attributes.FirstOrDefault(a => offset >= a.Start && offset < a.End)?.Name;
            var field = SelectedFields.FirstOrDefault(f => f.Attribute == name)
                ?? SelectedFields.FirstOrDefault(f => Regex.IsMatch(problem.Message, $@"\b{Regex.Escape(f.Attribute)}\b"));
            var text = $"{problem.Severity}: {problem.Message}";
            if (field is null) SelectedNodeProblems.Add(text);
            else field.Error = field.Error is null ? text : $"{field.Error}\n{text}";
        }
    }
}
