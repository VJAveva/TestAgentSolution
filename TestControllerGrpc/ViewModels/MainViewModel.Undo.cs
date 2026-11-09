using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Editing;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

// One edit path for the WatchList (ContextMenu_Requirement.md P3): every model change runs inside an edit
// scope, which records a snapshot for Undo, marks the WatchList dirty and queues validation.
public sealed partial class MainViewModel
{
    private readonly EditHistory _history = new();
    private int _editDepth;
    private string? _editBefore;
    private string _editLabel = "";

    public string UndoText => _history.UndoLabel is { } l ? $"Undo {l}" : "Undo";
    public string RedoText => _history.RedoLabel is { } l ? $"Redo {l}" : "Redo";

    /// <summary>Runs <paramref name="mutate"/> as one undoable step.</summary>
    public void ApplyEdit(string label, Action mutate)
    {
        using var _ = BeginEdit(label);
        mutate();
    }

    /// <summary>Opens an undoable step; nested scopes join the outermost one.</summary>
    private EditScope BeginEdit(string label)
    {
        if (_editDepth++ == 0)
        {
            _editLabel = label;
            _editBefore = _history.Begin(_config);
        }
        return new EditScope(this);
    }

    private void EndEdit()
    {
        if (--_editDepth > 0 || _editBefore is null) return;
        var before = _editBefore;
        _editBefore = null;
        if (!_history.Commit(_config, _editLabel, before)) return;

        IsDirty = true;
        QueueValidation();
        NotifyHistoryChanged();
    }

    private readonly struct EditScope(MainViewModel owner) : IDisposable
    {
        public void Dispose() => owner.EndEdit();
    }

    private bool CanUndo() => _history.CanUndo;
    private bool CanRedo() => _history.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var label = _history.UndoLabel;
        if (_history.Undo(_config) is { } restored) ReplaceConfigAfterHistoryStep(restored, $"Undo: {label}");
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var label = _history.RedoLabel;
        if (_history.Redo(_config) is { } restored) ReplaceConfigAfterHistoryStep(restored, $"Redo: {label}");
    }

    private void ReplaceConfigAfterHistoryStep(WatchListConfig restored, string message)
    {
        _config = restored;
        _executor.LoadTemplates(restored.Templates);
        if (_sessionManager.HasAnyActiveExecution) _watcherManager.ApplyDiff(restored.WatchItems);
        else _watcherManager.ApplyConfig(restored);

        SelectedNode = null;
        RebuildAllTrees();
        IsDirty = true;
        QueueValidation();
        NotifyHistoryChanged();
        AddLog(message);
    }

    /// <summary>Called whenever a different WatchList is loaded: old steps must never apply to it.</summary>
    private void ClearEditHistory()
    {
        _history.Clear();
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
    }
}
