using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.ViewModels;

/// <summary>
/// The live "running states" board. Folds the streaming record set into one row per
/// pipeline run (keyed by RunId) and keeps each row's state current as new records
/// arrive — Running until a completion marker is seen, then Completed or Failed.
/// Rows are ordered most-recently-active first so live runs stay at the top.
/// </summary>
public sealed partial class LiveStatusViewModel : ObservableObject
{
    // "[Session] Completed <id>", "ExecutionCompleted", "pipeline completed", "✓ Success".
    private static readonly Regex CompletionRegex = new(
        @"Completed\s+[0-9a-fA-F]{6,}|ExecutionCompleted|pipeline completed|\u2713\s*Success",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, RunStatusVM> _byRun = new(StringComparer.Ordinal);

    public ObservableCollection<RunStatusVM> Runs { get; } = new();

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private RunStatusVM? _selectedRun;

    /// <summary>Raised by "View logs" so the shell can switch to the Logs view pre-filtered.</summary>
    public event Action<LogFilterContext>? ViewLogsRequested;


    /// <summary>Reset the board (called when a fresh load / live session begins).</summary>
    public void Clear()
    {
        _byRun.Clear();
        Runs.Clear();
        RunningCount = FailedCount = CompletedCount = 0;
    }

    /// <summary>Seed the board from an initial batch of already-loaded records.</summary>
    public void SetRecords(IReadOnlyList<LogRecord> records)
    {
        Clear();
        Ingest(records);
    }

    /// <summary>Fold a batch of new records into the run rows, updating states in place.</summary>
    public void Ingest(IReadOnlyList<LogRecord> records)
    {
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.RunId)) continue;

            if (!_byRun.TryGetValue(r.RunId, out var vm))
            {
                vm = new RunStatusVM
                {
                    RunId = r.RunId,
                    Pipeline = r.Pipeline,
                    Agent = r.Agent,
                    StartedUtc = r.Timestamp,
                    LastUpdate = r.Timestamp,
                };
                _byRun[r.RunId] = vm;
                Runs.Insert(0, vm);
            }

            if (string.IsNullOrEmpty(vm.Pipeline) && !string.IsNullOrEmpty(r.Pipeline))
                vm.Pipeline = r.Pipeline;
            if (!string.IsNullOrEmpty(r.Agent))
                vm.Agent = r.Agent;
            if (!string.IsNullOrEmpty(r.Action))
                vm.LastAction = r.Action;
            if (r.Timestamp > vm.LastUpdate)
                vm.LastUpdate = r.Timestamp;

            if (r.IsError)
            {
                vm.ErrorCount++;
                vm.LastError = string.IsNullOrEmpty(r.Exception) ? r.Message : r.Exception;
            }

            var completed = CompletionRegex.IsMatch(r.Message);
            vm.State = completed
                ? (vm.ErrorCount > 0 ? RunState.Failed : RunState.Completed)
                : (vm.State == RunState.Completed ? RunState.Completed : RunState.Running);
        }

        ReorderAndCount();
    }

    private void ReorderAndCount()
    {
        // Most-recently-active first (stable enough for a modest live run count).
        var ordered = Runs.OrderByDescending(r => r.LastUpdate).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Runs.IndexOf(ordered[i]);
            if (current != i) Runs.Move(current, i);
        }

        RunningCount = _byRun.Values.Count(r => r.State == RunState.Running);
        FailedCount = _byRun.Values.Count(r => r.State == RunState.Failed);
        CompletedCount = _byRun.Values.Count(r => r.State == RunState.Completed);
    }

    [RelayCommand]
    private void ViewLogs(object? parameter)
    {
        var run = parameter as RunStatusVM ?? SelectedRun;
        if (run is null) return;
        ViewLogsRequested?.Invoke(new LogFilterContext
        {
            RunId = run.RunId,
            Agent = run.Agent,
        });
    }
}
