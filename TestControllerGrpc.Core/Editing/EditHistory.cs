using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Editing;

/// <summary>
/// Snapshot undo/redo for the in-memory WatchList (ContextMenu_Requirement.md rule 7). Each step stores the
/// whole WatchList as XML, using the same serializer as Save, so an undo can never produce a file Save could not.
/// </summary>
public sealed class EditHistory
{
    public const int Capacity = 50;

    private readonly LinkedList<Step> _undo = new();
    private readonly Stack<Step> _redo = new();

    private sealed record Step(string Label, string Snapshot);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.TryPeek(out var s) ? s.Label : null;
    public int UndoCount => _undo.Count;

    /// <summary>Runs <paramref name="mutate"/> and records it. Returns false (and records nothing) when it changed nothing.</summary>
    public bool Apply(WatchListConfig config, string label, Action mutate)
    {
        var before = Begin(config);
        mutate();
        return Commit(config, label, before);
    }

    /// <summary>Snapshot to pass to <see cref="Commit"/> once the edit is done.</summary>
    public string Begin(WatchListConfig config) => Snapshot(config);

    public bool Commit(WatchListConfig config, string label, string before)
    {
        if (Snapshot(config) == before) return false;

        _undo.AddLast(new Step(label, before));
        if (_undo.Count > Capacity) _undo.RemoveFirst();
        _redo.Clear();
        return true;
    }

    /// <summary>The config as it was before the last edit, or null when there is nothing to undo.</summary>
    public WatchListConfig? Undo(WatchListConfig current)
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(last.Value with { Snapshot = Snapshot(current) });
        return Restore(last.Value.Snapshot, current);
    }

    public WatchListConfig? Redo(WatchListConfig current)
    {
        if (!_redo.TryPop(out var step)) return null;
        _undo.AddLast(step with { Snapshot = Snapshot(current) });
        return Restore(step.Snapshot, current);
    }

    /// <summary>Called on load/reload: steps from another file must never be applied to this one.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static string Snapshot(WatchListConfig config) => WatchListXmlParser.SerializeWatchList(config);

    private static WatchListConfig Restore(string snapshot, WatchListConfig current)
    {
        var restored = WatchListXmlParser.DeserializeWatchList(snapshot)
                       ?? throw new InvalidOperationException("Undo snapshot is not a WatchList.");
        restored.FilePath = current.FilePath;
        return restored;
    }
}
