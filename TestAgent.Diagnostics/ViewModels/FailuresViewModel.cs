using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestAgent.Diagnostics.Models;
using TestAgent.Diagnostics.Services;

namespace TestAgent.Diagnostics.ViewModels;

/// <summary>
/// View A — the failure-first landing view. Failing runs, the selected run's action
/// sequence, the failed-action detail, and the "View logs" command that hands a filter
/// context to View B.
/// </summary>
public sealed partial class FailuresViewModel : ObservableObject
{
    private readonly FailureAnalyzer _analyzer = new();
    private IReadOnlyList<FailingRunVM> _allRuns = Array.Empty<FailingRunVM>();
    private ICollectionView? _view;

    public ObservableCollection<FailingRunVM> Runs { get; } = new();
    public ObservableCollection<string> Pipelines { get; } = new();
    public ObservableCollection<string> Agents { get; } = new();

    [ObservableProperty] private string _pipelineFilter = "All";
    [ObservableProperty] private string _agentFilter = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun))]
    private FailingRunVM? _selectedRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStep))]
    private ActionStepVM? _selectedStep;

    [ObservableProperty] private int _failureCount;

    public bool HasSelectedRun => SelectedRun is not null;
    public bool HasSelectedStep => SelectedStep is not null;

    /// <summary>Raised by "View logs" so the shell can switch to View B pre-filtered.</summary>
    public event Action<LogFilterContext>? ViewLogsRequested;

    public void SetRecords(IReadOnlyList<LogRecord> records)
    {
        _allRuns = _analyzer.BuildFailingRuns(records);

        Runs.Clear();
        foreach (var r in _allRuns) Runs.Add(r);

        Pipelines.Clear();
        Pipelines.Add("All");
        foreach (var p in _allRuns.Select(r => r.Pipeline).Distinct().OrderBy(p => p))
            Pipelines.Add(p);

        Agents.Clear();
        Agents.Add("All");
        foreach (var a in _allRuns.Select(r => r.Agent)
                     .Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a))
            Agents.Add(a);

        _view = CollectionViewSource.GetDefaultView(Runs);
        _view.Filter = FilterPredicate;
        FailureCount = _allRuns.Count;
        SelectedRun = null;
        SelectedStep = null;
        _view.Refresh();
    }

    [RelayCommand]
    private void ViewLogs(object? parameter)
    {
        var step = parameter as ActionStepVM ?? SelectedStep;
        if (SelectedRun is null) return;
        ViewLogsRequested?.Invoke(LogFilterContext.ForRun(SelectedRun, step));
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not FailingRunVM r) return false;
        if (PipelineFilter != "All" && !string.Equals(r.Pipeline, PipelineFilter, StringComparison.Ordinal))
            return false;
        if (AgentFilter != "All" && !string.Equals(r.Agent, AgentFilter, StringComparison.Ordinal))
            return false;
        return true;
    }

    partial void OnPipelineFilterChanged(string value) => _view?.Refresh();
    partial void OnAgentFilterChanged(string value) => _view?.Refresh();

    partial void OnSelectedRunChanged(FailingRunVM? value)
    {
        // Default the failed-action detail to the run's first failed step.
        SelectedStep = value?.Steps.FirstOrDefault(s => s.Status == StepStatus.Failed);
    }
}
