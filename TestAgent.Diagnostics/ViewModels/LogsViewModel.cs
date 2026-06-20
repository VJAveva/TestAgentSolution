using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.ViewModels;

/// <summary>
/// View B — the Logs detail view. Virtualized grid over the shared record set with a
/// full filter set, a details / stack-trace pane, export, and the "filtered by" chip.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private IReadOnlyList<LogRecord> _all = Array.Empty<LogRecord>();
    private ICollectionView? _view;

    public ObservableCollection<LogRecord> Records { get; } = new();

    public ObservableCollection<string> Components { get; } = new();
    public ObservableCollection<string> Agents { get; } = new();
    public ObservableCollection<string> Severities { get; } =
        new() { "All", "Debug", "Info", "Warning", "Error" };

    [ObservableProperty] private string _componentFilter = "All";
    [ObservableProperty] private string _agentFilter = "All";
    [ObservableProperty] private string _severityFilter = "All";
    [ObservableProperty] private string _runIdFilter = "";
    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private LogRecord? _selectedRecord;
    [ObservableProperty] private string _contextChip = "";
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _totalCount;

    public bool HasContext => !string.IsNullOrEmpty(ContextChip);

    /// <summary>Replace the shared record set (called once after a load).</summary>
    public void SetRecords(IReadOnlyList<LogRecord> records)
    {
        _all = records;
        Records.Clear();
        foreach (var r in records) Records.Add(r);

        Components.Clear();
        Components.Add("All");
        foreach (var c in records.Select(r => r.Component)
                     .Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c))
            Components.Add(c);

        Agents.Clear();
        Agents.Add("All");
        foreach (var a in records.Select(r => r.Agent)
                     .Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a))
            Agents.Add(a);

        _view = CollectionViewSource.GetDefaultView(Records);
        _view.Filter = FilterPredicate;
        TotalCount = records.Count;
        Refresh();
    }

    /// <summary>Arrive pre-filtered from View A.</summary>
    public void ApplyContext(LogFilterContext ctx)
    {
        RunIdFilter = ctx.RunId;
        AgentFilter = string.IsNullOrEmpty(ctx.Agent) || !Agents.Contains(ctx.Agent) ? "All" : ctx.Agent;
        SeverityFilter = "All";
        ComponentFilter = "All";
        TextFilter = "";
        ContextChip = ctx.Describe();
        OnPropertyChanged(nameof(HasContext));
        Refresh();

        // Land on the failing line if present.
        SelectedRecord = _all.FirstOrDefault(r =>
            r.RunId == ctx.RunId && r.IsError) ?? _all.FirstOrDefault(r => r.RunId == ctx.RunId);
    }

    [RelayCommand]
    private void ClearFilter()
    {
        RunIdFilter = "";
        AgentFilter = "All";
        SeverityFilter = "All";
        ComponentFilter = "All";
        TextFilter = "";
        ContextChip = "";
        OnPropertyChanged(nameof(HasContext));
        Refresh();
    }

    [RelayCommand]
    private void Export()
    {
        if (_view is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|Log files (*.log)|*.log|Text files (*.txt)|*.txt",
            FileName = $"DiagnosticsLog_{DateTime.Now:yyyyMMdd_HHmmss}",
            Title = "Export filtered logs",
        };
        if (dlg.ShowDialog() != true) return;

        var rows = _view.Cast<LogRecord>().ToList();
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        using var w = new StreamWriter(dlg.FileName);
        if (ext == ".csv")
        {
            w.WriteLine("Timestamp,Severity,Component,Agent,RunId,Message");
            foreach (var r in rows)
                w.WriteLine($"{Csv(r.TimeText)},{Csv(r.SeverityText)},{Csv(r.Component)}," +
                            $"{Csv(r.Agent)},{Csv(r.RunId)},{Csv(r.Message)}");
        }
        else
        {
            foreach (var r in rows)
                w.WriteLine($"{r.TimeText} [{r.SeverityText}] [{r.Component}] {r.Message}");
        }
    }

    private static string Csv(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    private bool FilterPredicate(object obj)
    {
        if (obj is not LogRecord r) return false;

        if (ComponentFilter != "All" && !string.Equals(r.Component, ComponentFilter, StringComparison.Ordinal))
            return false;
        if (AgentFilter != "All" && !string.Equals(r.Agent, AgentFilter, StringComparison.Ordinal))
            return false;
        if (SeverityFilter != "All"
            && Enum.TryParse<Severity>(SeverityFilter, out var floor)
            && r.Severity < floor)
            return false;
        if (!string.IsNullOrEmpty(RunIdFilter)
            && !r.RunId.Contains(RunIdFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(TextFilter)
            && !r.Message.Contains(TextFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void Refresh()
    {
        _view?.Refresh();
        VisibleCount = _view?.Cast<object>().Count() ?? 0;
    }

    partial void OnComponentFilterChanged(string value) => Refresh();
    partial void OnAgentFilterChanged(string value) => Refresh();
    partial void OnSeverityFilterChanged(string value) => Refresh();
    partial void OnRunIdFilterChanged(string value) => Refresh();
    partial void OnTextFilterChanged(string value) => Refresh();
    partial void OnContextChipChanged(string value) => OnPropertyChanged(nameof(HasContext));
}
