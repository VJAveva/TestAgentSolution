using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>A template the editor will write: its ID when the editor opened (null if created here) and its document.</summary>
public sealed record PipelineEditorTemplateChange(string? OriginalId, string Xml);

/// <summary>
/// The Pipeline Editor window: one pipeline tab plus a tab per template opened from it (section 4). Each tab is a
/// <see cref="WatchItemXmlEditorViewModel"/> with its own undo history; Save checks and saves them together.
/// </summary>
public sealed partial class PipelineEditorSessionViewModel : ObservableObject
{
    private readonly WatchListConfig? _context;
    private readonly IReadOnlyCollection<XmlEditorAgent>? _agents;
    private readonly IPreflightFileSystem? _fileSystem;

    public PipelineEditorSessionViewModel(
        WatchItemXmlEditorViewModel pipeline,
        WatchListConfig? context = null,
        IReadOnlyCollection<XmlEditorAgent>? agents = null,
        IPreflightFileSystem? fileSystem = null)
    {
        _context = context;
        _agents = agents;
        _fileSystem = fileSystem;
        Pipeline = pipeline;
        Attach(pipeline);
        Tabs.Add(pipeline);
        _selectedTab = pipeline;
    }

    public WatchItemXmlEditorViewModel Pipeline { get; }
    public ObservableCollection<WatchItemXmlEditorViewModel> Tabs { get; } = new();

    [ObservableProperty] private WatchItemXmlEditorViewModel _selectedTab;
    [ObservableProperty] private string _windowTitle = "Pipeline Editor";

    /// <summary>Why templates are read-only for this user (no Template_Edit); null when they can be edited.</summary>
    public Func<string?>? TemplateEditBlocked { get; set; }

    /// <summary>The live, saved XML of a tab's pipeline or template (null when it no longer exists), for conflict checks.</summary>
    public Func<WatchItemXmlEditorViewModel, string?>? LatestSavedXml { get; set; }

    /// <summary>Asked when something changed elsewhere since it was opened: true = reload it (drop this tab's edits).</summary>
    public Func<string, bool>? ConfirmReload { get; set; }

    /// <summary>Yes/no question to the user (what Save will write, discard a tab); false cancels.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Shows side-by-side comparisons (What changed, or yours vs. the latest saved version).</summary>
    public Action<string, IReadOnlyList<PipelineChange>>? ShowChanges { get; set; }

    /// <summary>Before / after for every changed tab; secrets masked. A template created here is compared with nothing.</summary>
    public IReadOnlyList<PipelineChange> Changes() =>
        Tabs.Where(IsChanged)
            .Select(t => PipelineDiff.Compare(Describe(t).TrimStart('-', ' '), t.IsTemplateTab && t.TemplateOriginalId is null ? "" : t.OriginalXml, t.XmlText))
            .ToList();

    [RelayCommand]
    private void WhatChanged()
    {
        var changes = Changes();
        if (changes.Count == 0)
        {
            SelectedTab.StatusMessage = "No changes since the editor opened.";
            SelectedTab.StatusSeverity = "Info";
            return;
        }
        ShowChanges?.Invoke("What changed", changes);
    }

    public event Action? RequestClose;
    public bool DialogAccepted { get; private set; }
    public string? PipelineResultXml { get; private set; }
    public IReadOnlyList<PipelineEditorTemplateChange> TemplateResults { get; private set; } = [];

    public bool HasUnsavedChanges => !DialogAccepted && Tabs.Any(IsChanged);

    /// <summary>Opens the template's tab (or activates it) and selects the step the user came from.</summary>
    public void OpenTemplate(string templateId, string? templatePath)
    {
        var tab = Tabs.FirstOrDefault(t => t.IsTemplateTab && string.Equals(TemplateIdOf(t), templateId, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            var template = _context?.Templates.FirstOrDefault(t => string.Equals(t.ID, templateId, StringComparison.OrdinalIgnoreCase));
            if (template is null)
            {
                SelectedTab.StatusMessage = $"Template '{templateId}' is not in the Library.";
                SelectedTab.StatusSeverity = "Error";
                return;
            }
            tab = AddTemplateTab(PipelineTemplateEdits.TemplateDocument(template), template.ID);
        }
        SelectedTab = tab;
        tab.RefreshStructure();
        tab.SelectPath(templatePath ?? PipelinePaths.Template(0));
    }

    [RelayCommand]
    private void CloseTab(WatchItemXmlEditorViewModel? tab)
    {
        if (tab is null || ReferenceEquals(tab, Pipeline)) return;
        if (IsChanged(tab) && Confirm?.Invoke($"Discard your changes to {tab.TabTitle}?") != true) return;
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = Pipeline;
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogAccepted = false;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Save()
    {
        var changed = Tabs.Where(IsChanged).ToList();
        if (changed.Count == 0)
        {
            SelectedTab.StatusMessage = "Nothing to save.";
            SelectedTab.StatusSeverity = "Info";
            return;
        }

        foreach (var tab in changed)
        {
            if (tab.CheckReadyToSave()) continue;
            SelectedTab = tab;
            return;
        }

        // No silent overwrite: something saved since this editor opened wins until the user decides.
        foreach (var tab in changed.Where(t => t.TemplateOriginalId is not null || !t.IsTemplateTab))
        {
            var latest = LatestSavedXml?.Invoke(tab);
            if (latest is null || latest == tab.OriginalXml) continue;
            SelectedTab = tab;
            var message = $"{tab.TabTitle} was changed by someone else (or WatchList.xml was reloaded) since you opened it.";
            tab.StatusMessage = message + " Nothing was saved.";
            tab.StatusSeverity = "Error";
            ShowChanges?.Invoke("Changed since you opened it",
                [PipelineDiff.Compare($"{tab.TabTitle}: latest saved (left) and yours (right)", latest, tab.XmlText)]);
            if (ConfirmReload?.Invoke(message + "\n\nYes = load the latest version (your changes to it are dropped).\nNo = keep editing.") == true)
                tab.Reload(latest);
            return;
        }

        var templates = changed.Where(t => t.IsTemplateTab).ToList();
        var parsed = templates.Select(t => (t.TemplateOriginalId, Template: TryParseTemplate(t.XmlText)))
            .Where(p => p.Template is not null).Select(p => (p.TemplateOriginalId, p.Template!)).ToList();
        if (_context is not null && parsed.Count > 0)
        {
            var editedPipeline = IsChanged(Pipeline) && !Pipeline.IsTemplateTab ? TryParsePipeline(Pipeline.XmlText) : null;
            var problems = PipelineTemplateEdits.CheckPipelinesUsing(
                _context, parsed, editedPipeline, _agents?.Select(a => a.Name).ToArray(), _fileSystem);
            if (problems.Count > 0)
            {
                var tab = templates[0];
                SelectedTab = tab;
                tab.ShowImpactProblems(problems);
                return;
            }
        }

        if (Confirm?.Invoke("Save will write:\n" + string.Join("\n", changed.Select(Describe))) == false) return;

        PipelineResultXml = IsChanged(Pipeline) ? Pipeline.XmlText : null;
        TemplateResults = templates.Select(t => new PipelineEditorTemplateChange(t.TemplateOriginalId, t.XmlText)).ToList();
        DialogAccepted = true;
        RequestClose?.Invoke();
    }

    private string Describe(WatchItemXmlEditorViewModel tab)
    {
        if (!tab.IsTemplateTab) return $"- {tab.TabTitle}";
        if (tab.TemplateOriginalId is null) return $"- {tab.TabTitle} (new)";
        var users = _context is null ? [] : TemplateUsage.PipelinesReferencing(_context, tab.TemplateOriginalId);
        return $"- {tab.TabTitle} (affects {users.Count} pipeline{(users.Count == 1 ? "" : "s")})";
    }

    private WatchItemXmlEditorViewModel AddTemplateTab(string document, string? originalId)
    {
        var id = originalId ?? TemplateIdOf(document) ?? "";
        var tab = new WatchItemXmlEditorViewModel(document, _context, _agents?.Select(a => a.Name).ToArray(), _fileSystem, _agents)
        {
            TemplateOriginalId = originalId,
            TabTitle = $"Template: {id}",
            ReadOnlyReason = TemplateEditBlocked?.Invoke() is { } blocked ? $"You can view but not edit templates ({blocked})." : null,
        };
        var users = _context is null || originalId is null ? [] : TemplateUsage.PipelinesReferencing(_context, originalId);
        tab.Banner = originalId is null
            ? $"New template '{id}' - it is written to the Library when you save."
            : $"Template '{id}' is used by {users.Count} pipeline{(users.Count == 1 ? "" : "s")}"
              + (users.Count == 0 ? "." : $": {string.Join(", ", users)}. Changes apply to all of them.")
              + (tab.ReadOnlyReason is { } reason ? $" {reason}" : "");
        Attach(tab);
        Tabs.Add(tab);
        return tab;
    }

    private void Attach(WatchItemXmlEditorViewModel tab)
    {
        tab.ContextProvider = WorkingContext;
        tab.TemplateSource = TemplateText;
        tab.TemplateEditBlocked = () => TemplateEditBlocked?.Invoke();
        tab.EditTemplateRequested += OpenTemplate;
        tab.TemplateCreated += (id, document) =>
        {
            SelectedTab = AddTemplateTab(document, null);
            SelectedTab.SelectPath(PipelinePaths.Template(0));
        };
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WatchItemXmlEditorViewModel.HasUnsavedChanges))
                OnPropertyChanged(nameof(HasUnsavedChanges));
        };
    }

    /// <summary>The Library as it will be after Save: open template tabs replace (or add to) the saved templates.</summary>
    private WatchListConfig? WorkingContext()
    {
        if (_context is null) return null;
        var open = Tabs.Where(t => t.IsTemplateTab)
            .Select(t => (t.TemplateOriginalId, Template: TryParseTemplate(t.XmlText)))
            .Where(o => o.Template is not null)
            .ToList();
        if (open.Count == 0) return _context;
        var replaced = open.Select(o => o.TemplateOriginalId ?? o.Template!.ID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new WatchListConfig
        {
            WatchItems = _context.WatchItems,
            Templates = [.. _context.Templates.Where(t => !replaced.Contains(t.ID)), .. open.Select(o => o.Template!)],
            GlobalVariablesFile = _context.GlobalVariablesFile,
            FilePath = _context.FilePath,
        };
    }

    private static TemplateConfig? TryParseTemplate(string xml)
    {
        try { return WatchListXmlParser.DeserializeTemplateList(xml)?.FirstOrDefault(); }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>An open template tab's current text wins over the Library copy, so a Ref shows the edits.</summary>
    private string? TemplateText(string templateId) =>
        Tabs.FirstOrDefault(t => t.IsTemplateTab && string.Equals(TemplateIdOf(t), templateId, StringComparison.OrdinalIgnoreCase))?.XmlText;

    private static bool IsChanged(WatchItemXmlEditorViewModel tab) => tab.TemplateOriginalId is null && tab.IsTemplateTab || tab.XmlText != tab.OriginalXml;

    private static string? TemplateIdOf(WatchItemXmlEditorViewModel tab) => tab.TemplateOriginalId ?? TemplateIdOf(tab.XmlText);

    private static string? TemplateIdOf(string document) =>
        PipelineXmlMap.TryBuild(document) is { Roots: [{ Kind: NodeKinds.Template } template, ..] } ? template.Name : null;

    private static WatchItemConfig? TryParsePipeline(string xml)
    {
        try { return WatchListXmlParser.DeserializeWatchItem(xml); }
        catch (System.Xml.XmlException) { return null; }
    }
}
