using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

/// <summary>
/// Backing view model for the Agents ▸ Maintenance tab (spec R7/R11/R14): the in-flight operations (live phase +
/// progress + provenance) and the completed-operation history read from <see cref="IMaintenanceOperationStore"/>.
/// </summary>
public partial class MaintenanceVM : ObservableObject, IDisposable
{
    private readonly IFleetMaintenanceService? _service;
    private readonly IMaintenanceOperationStore? _store;
    private readonly Dispatcher _uiDispatcher;
    private bool _disposed;

    public ObservableCollection<MaintenanceOpVM> ActiveOperations { get; } = new();
    public ObservableCollection<MaintenanceOpVM> History { get; } = new();

    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private bool _isLoadingHistory;

    public bool HasActive => ActiveCount > 0;
    partial void OnActiveCountChanged(int value) => OnPropertyChanged(nameof(HasActive));

    /// <summary>Windows Update rollup and per-node rows rendered in this tab (R19); null until attached.</summary>
    public FleetUpdatesVM? Updates { get; private set; }

    public bool HasUpdates => Updates is not null;

    public void AttachUpdates(FleetUpdatesVM updates)
    {
        Updates = updates;
        OnPropertyChanged(nameof(Updates));
        OnPropertyChanged(nameof(HasUpdates));
    }

    public MaintenanceVM(IFleetMaintenanceService? service, IMaintenanceOperationStore? store, Dispatcher uiDispatcher)
    {
        _service = service;
        _store = store;
        _uiDispatcher = uiDispatcher;

        if (_service is not null)
        {
            _service.ProgressChanged += OnProgress;
            _service.OperationCompleted += OnCompleted;
            SyncActive();
        }

        _ = LoadHistoryAsync();
    }

    private void OnProgress(object? sender, MaintenanceProgress p)
    {
        _uiDispatcher.InvokeAsync(() =>
        {
            SyncActive();
            ActiveOperations.FirstOrDefault(o => o.OperationId == p.OperationId)?.ApplyProgress(p);
        });
    }

    private void OnCompleted(object? sender, MaintenanceOperation op)
    {
        _uiDispatcher.InvokeAsync(async () =>
        {
            SyncActive();
            await LoadHistoryAsync();
        });
    }

    private void SyncActive()
    {
        if (_service is null) return;
        var current = _service.ActiveOperations.ToList();

        for (int i = ActiveOperations.Count - 1; i >= 0; i--)
            if (!current.Any(o => o.Id == ActiveOperations[i].OperationId))
                ActiveOperations.RemoveAt(i);

        foreach (var op in current)
            if (ActiveOperations.All(o => o.OperationId != op.Id))
                ActiveOperations.Add(new MaintenanceOpVM(op, isActive: true));

        ActiveCount = ActiveOperations.Count;
    }

    private async Task LoadHistoryAsync()
    {
        if (_store is null) return;
        IsLoadingHistory = true;
        try
        {
            var rows = await _store.GetHistoryAsync(null, null, null, CancellationToken.None);
            await _uiDispatcher.InvokeAsync(() =>
            {
                History.Clear();
                foreach (var op in rows)
                    History.Add(new MaintenanceOpVM(op, isActive: false));
            });
        }
        catch
        {
            // History is best-effort; a read failure should not break the tab.
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    [RelayCommand]
    private async Task RefreshHistory() => await LoadHistoryAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_service is not null)
        {
            _service.ProgressChanged -= OnProgress;
            _service.OperationCompleted -= OnCompleted;
        }
    }
}

/// <summary>One row in the Maintenance tab — a single operation, active or historical.</summary>
public partial class MaintenanceOpVM : ObservableObject
{
    public MaintenanceOpVM(MaintenanceOperation op, bool isActive)
    {
        OperationId = op.Id;
        NodeId = op.NodeId;
        KindText = op.Kind.ToString();
        SnapshotName = string.IsNullOrEmpty(op.SnapshotName) ? "—" : op.SnapshotName!;
        ScriptPath = string.IsNullOrEmpty(op.ScriptPath) ? "—" : op.ScriptPath!;
        TriggerSourceText = op.TriggerSource.ToString();
        TriggeredBy = string.IsNullOrEmpty(op.TriggeredBy) ? "—" : op.TriggeredBy!;
        Reason = string.IsNullOrEmpty(op.Reason) ? "—" : op.Reason!;
        LinkedRun = op.LinkedRunId?.ToString() ?? "—";
        StartedText = op.StartedUtc.ToLocalTime().ToString("MMM d HH:mm");
        DurationText = op.CompletedUtc is { } done ? FormatSpan(done - op.StartedUtc) : "";
        ResultText = op.State.ToString();
        LogPath = op.LogPath ?? "";
        IsActive = isActive;

        _stateText = op.State.ToString();
        _phaseText = op.Phase.ToString();
    }

    public Guid OperationId { get; }
    public string NodeId { get; }
    public string KindText { get; }
    public string SnapshotName { get; }
    public string ScriptPath { get; }
    public string TriggerSourceText { get; }
    public string TriggeredBy { get; }
    public string Reason { get; }
    public string LinkedRun { get; }
    public string StartedText { get; }
    public string DurationText { get; }
    public string ResultText { get; }
    public string LogPath { get; }
    public bool IsActive { get; }

    [ObservableProperty] private string _stateText;
    [ObservableProperty] private string _phaseText;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private int _stepNumber;
    [ObservableProperty] private int _stepCount;
    [ObservableProperty] private string _elapsed = "";

    public int ProgressPercent => StepCount > 0 ? (int)(100.0 * StepNumber / StepCount) : 0;

    public void ApplyProgress(MaintenanceProgress p)
    {
        PhaseText = p.Phase.ToString();
        StepNumber = p.StepNumber;
        StepCount = p.StepCount;
        Message = p.Message;
        Elapsed = FormatSpan(p.Elapsed);
        OnPropertyChanged(nameof(ProgressPercent));
    }

    private static string FormatSpan(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
