using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.WatchBuilder;

// =============================================================================
// WatchItem Builder — root view-model (Tier3 §5)
//
// Edits a working COPY of the vocabulary config (deep-cloned via the existing
// serializer round-trip so the running controller's live config is untouched
// until Save). Every node edit bubbles here, re-serialising the live XML pane
// and re-running WatchListValidator.Analyze(). Save is blocked whenever any
// Error-severity issue exists, satisfying the acceptance criterion:
// "cannot save XML that fails to load". Opening a legacy file and re-saving
// round-trips it losslessly through the current serializer.
// =============================================================================
public sealed partial class WatchBuilderViewModel : ObservableObject
{
    private readonly IVocabularyMonitor _monitor;
    private readonly IWatchListXmlParser _parser;
    private readonly IAppLogger _logger;

    private WatchListConfig _config = new();

    public WatchBuilderViewModel(
        IVocabularyMonitor monitor,
        IWatchListXmlParser parser,
        IAppLogger logger)
    {
        _monitor = monitor;
        _parser = parser;
        _logger = logger;
        Load();
    }

    /// <summary>Root WatchItem nodes.</summary>
    public ObservableCollection<WatchBuilderNodeViewModel> Roots { get; } = new();

    /// <summary>Validation issues from the last <see cref="WatchListValidator.Analyze"/> pass.</summary>
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    [ObservableProperty]
    private WatchBuilderNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _liveXml = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasBlockingErrors;

    [ObservableProperty]
    private string _statusMessage = "";

    partial void OnSelectedNodeChanged(WatchBuilderNodeViewModel? oldValue, WatchBuilderNodeViewModel? newValue)
    {
        AddChildCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        AddEventCommand.NotifyCanExecuteChanged();
        AddGroupCommand.NotifyCanExecuteChanged();
        AddActionCommand.NotifyCanExecuteChanged();
        AddInitializeCommand.NotifyCanExecuteChanged();
        AddRefCommand.NotifyCanExecuteChanged();
    }

    // ── Load / clone ─────────────────────────────────────────────────────────
    [RelayCommand]
    private void Load()
    {
        var source = _monitor.CurrentConfig;
        var path = source?.FilePath ?? "";

        WatchListConfig working;
        if (source is not null)
        {
            // Deep-clone via serializer round-trip so we never mutate the live config.
            var xml = _parser.SerializeWatchList(source);
            working = _parser.DeserializeWatchList(xml) ?? new WatchListConfig();
        }
        else
        {
            working = new WatchListConfig();
        }

        working.FilePath = path;
        _config = working;
        FilePath = path;

        Roots.Clear();
        foreach (var item in _config.WatchItems)
        {
            var vm = new WatchItemNodeViewModel(item, null);
            vm.WireEdited(OnNodeEdited);
            Roots.Add(vm);
        }

        Refresh();
        _logger.Info("WatchBuilder", $"Loaded {_config.WatchItems.Count} WatchItem(s) from '{path}'.");
    }

    // ── Edit propagation ─────────────────────────────────────────────────────
    private void OnNodeEdited() => Refresh();

    private void Refresh()
    {
        LiveXml = _parser.SerializeWatchList(_config);

        Issues.Clear();
        foreach (var issue in WatchListValidator.Analyze(_config))
            Issues.Add(issue);

        HasBlockingErrors = Issues.Any(i => i.Severity == WatchIssueSeverity.Error);
        StatusMessage = HasBlockingErrors
            ? $"{Issues.Count(i => i.Severity == WatchIssueSeverity.Error)} error(s) block save."
            : $"Valid. {Issues.Count} advisory issue(s).";
    }

    // ── Add / delete ─────────────────────────────────────────────────────────
    [RelayCommand]
    private void AddWatchItem()
    {
        var item = new WatchItemConfig { Tag = "NewWatchItem" };
        _config.WatchItems.Add(item);
        var vm = new WatchItemNodeViewModel(item, null);
        vm.WireEdited(OnNodeEdited);
        Roots.Add(vm);
        SelectedNode = vm;
        Refresh();
    }

    private bool CanAddKind(WatchNodeKind kind) =>
        SelectedNode is not null && SelectedNode.AddableKinds.Contains(kind);

    [RelayCommand(CanExecute = nameof(CanAddEvent))]
    private void AddEvent() => AddKindToSelected(WatchNodeKind.Event);
    private bool CanAddEvent() => CanAddKind(WatchNodeKind.Event);

    [RelayCommand(CanExecute = nameof(CanAddGroup))]
    private void AddGroup() => AddKindToSelected(WatchNodeKind.ActionGroup);
    private bool CanAddGroup() => CanAddKind(WatchNodeKind.ActionGroup);

    [RelayCommand(CanExecute = nameof(CanAddAction))]
    private void AddAction() => AddKindToSelected(WatchNodeKind.Action);
    private bool CanAddAction() => CanAddKind(WatchNodeKind.Action);

    [RelayCommand(CanExecute = nameof(CanAddInitialize))]
    private void AddInitialize() => AddKindToSelected(WatchNodeKind.Initialize);
    private bool CanAddInitialize() => CanAddKind(WatchNodeKind.Initialize);

    [RelayCommand(CanExecute = nameof(CanAddRef))]
    private void AddRef() => AddKindToSelected(WatchNodeKind.Ref);
    private bool CanAddRef() => CanAddKind(WatchNodeKind.Ref);

    // Generic entry point used by the tree context menu.
    [RelayCommand(CanExecute = nameof(CanAddChild))]
    private void AddChild(WatchNodeKind kind) => AddKindToSelected(kind);
    private bool CanAddChild(WatchNodeKind kind) => CanAddKind(kind);

    private void AddKindToSelected(WatchNodeKind kind)
    {
        if (SelectedNode is null) return;
        var created = SelectedNode.AddChild(kind);
        if (created is not null)
        {
            created.WireEdited(OnNodeEdited);
            SelectedNode.IsExpanded = true;
            SelectedNode = created;
        }
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        var target = SelectedNode;
        if (target is null) return;

        if (target.Parent is null && target is WatchItemNodeViewModel wi)
        {
            _config.WatchItems.Remove(wi.Model);
            Roots.Remove(wi);
        }
        else
        {
            target.Parent?.RemoveChild(target);
        }

        SelectedNode = null;
        Refresh();
    }

    private bool CanDeleteSelected() => SelectedNode is not null;

    // ── Save ─────────────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (HasBlockingErrors) return;

        var path = string.IsNullOrWhiteSpace(FilePath) ? _config.FilePath : FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "No file path — cannot save.";
            _logger.Warn("WatchBuilder", "Save aborted: no vocabulary file path is set.");
            return;
        }

        try
        {
            _config.FilePath = path;
            _monitor.SuppressNextReload();
            _parser.Save(_config, path);
            StatusMessage = $"Saved to {path}.";
            _logger.Info("WatchBuilder", $"Saved {_config.WatchItems.Count} WatchItem(s) to '{path}'.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            _logger.Error("WatchBuilder", $"Save to '{path}' failed.", ex);
        }
    }

    private bool CanSave() => !HasBlockingErrors;
}
