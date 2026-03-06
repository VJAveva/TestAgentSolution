using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestAgentDisplay.Services;
using TestAgentGrpc;

namespace TestAgentDisplay.ViewModels;

/// <summary>
/// ViewModel for the unified audit timeline — merges events from all
/// connected agents into a single sorted view.
/// </summary>
public sealed partial class AuditTimelineViewModel : ObservableObject, IDisposable
{
    private readonly AgentConnectionManager _connectionManager;

    [ObservableProperty] private string _selectedTimeRange = "24h";
    [ObservableProperty] private string _selectedAgentFilter = "All";
    [ObservableProperty] private string _eventTypeFilter = "All";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Select a time range to load audit entries.";

    public ObservableCollection<TimelineEntryVM> TimelineEntries { get; } = new();
    public ObservableCollection<string> AvailableAgents { get; } = new() { "All" };
    public ObservableCollection<string> AvailableEventTypes { get; } = new()
    {
        "All", "Command*", "Heartbeat*", "Registration*", "Agent*", "Controller*"
    };
    public ObservableCollection<string> TimeRanges { get; } = new()
    {
        "Live", "1h", "6h", "24h"
    };

    // Cache to avoid re-fetching on tab switch
    private readonly List<TimelineEntryVM> _cachedEntries = new();
    private string? _lastFetchKey;

    // Live mode
    private bool _liveMode;

    public AuditTimelineViewModel(AgentConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        _connectionManager.EventReceived += OnLiveEventReceived;
        _connectionManager.ConnectionStateChanged += OnConnectionChanged;
    }

    partial void OnSelectedTimeRangeChanged(string value)
    {
        _liveMode = value == "Live";
        _ = RefreshAsync();
    }

    partial void OnSelectedAgentFilterChanged(string value) => ApplyFilters();
    partial void OnEventTypeFilterChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Loading…";

        try
        {
            var (fromDate, toDate) = GetDateRange();
            var eventFilter = EventTypeFilter is "All" ? null : EventTypeFilter;

            var allEntries = new List<TimelineEntryVM>();

            foreach (var address in _connectionManager.ConnectedAddresses)
            {
                var agentName = new Uri(address).Host;

                var reply = await _connectionManager.GetAuditLogAsync(
                    address, fromDate, toDate, eventFilter, maxEntries: 500);

                if (reply is null) continue;

                foreach (var e in reply.Entries)
                {
                    allEntries.Add(TimelineEntryVM.FromProto(e, agentName));
                }
            }

            // Sort newest first
            allEntries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

            _cachedEntries.Clear();
            _cachedEntries.AddRange(allEntries);
            _lastFetchKey = $"{SelectedTimeRange}_{SelectedAgentFilter}_{EventTypeFilter}";

            // Update available agents
            var agents = allEntries.Select(e => e.AgentName).Distinct().OrderBy(n => n).ToList();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableAgents.Clear();
                AvailableAgents.Add("All");
                foreach (var a in agents)
                    AvailableAgents.Add(a);
            });

            ApplyFilters();
            StatusText = $"Loaded {allEntries.Count} entries from {_connectionManager.ConnectedAddresses.Count()} agent(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilters()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            TimelineEntries.Clear();

            foreach (var entry in _cachedEntries)
            {
                if (SelectedAgentFilter != "All" && entry.AgentName != SelectedAgentFilter)
                    continue;

                if (!string.IsNullOrEmpty(SearchText) &&
                    !entry.Summary.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
                    !entry.DetailText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    continue;

                TimelineEntries.Add(entry);
            }
        });
    }

    private (string? FromDate, string? ToDate) GetDateRange()
    {
        var today = DateTime.UtcNow.Date;
        return SelectedTimeRange switch
        {
            "Live" => (today.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
            "1h"   => (today.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
            "6h"   => (today.AddDays(-1).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
            "24h"  => (today.AddDays(-1).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
            _      => (today.AddDays(-1).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
        };
    }

    // ?? Live mode ??????????????????????????????????????????????????????

    private void OnLiveEventReceived(string address, ExecutionEvent evt)
    {
        if (!_liveMode) return;
        if (evt.EventType is ExecutionEventType.EventStdoutLine or ExecutionEventType.EventStderrLine)
            return; // Skip noisy output lines in timeline

        var agentName = new Uri(address).Host;
        var entry = TimelineEntryVM.FromLiveEvent(evt, agentName);

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _cachedEntries.Insert(0, entry);
            if (_cachedEntries.Count > 2000)
                _cachedEntries.RemoveAt(_cachedEntries.Count - 1);

            // Apply filter inline
            if (SelectedAgentFilter != "All" && entry.AgentName != SelectedAgentFilter)
                return;
            if (!string.IsNullOrEmpty(SearchText) &&
                !entry.Summary.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                return;

            TimelineEntries.Insert(0, entry);
            if (TimelineEntries.Count > 2000)
                TimelineEntries.RemoveAt(TimelineEntries.Count - 1);
        });
    }

    private void OnConnectionChanged(string address, bool connected)
    {
        var agentName = new Uri(address).Host;
        var status = connected ? "Connected" : "Disconnected";
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!AvailableAgents.Contains(agentName))
                AvailableAgents.Add(agentName);
        });
    }

    public void Dispose()
    {
        _connectionManager.EventReceived -= OnLiveEventReceived;
        _connectionManager.ConnectionStateChanged -= OnConnectionChanged;
    }
}

/// <summary>
/// Single entry in the unified audit timeline.
/// </summary>
public sealed partial class TimelineEntryVM : ObservableObject
{
    public DateTime Timestamp { get; init; }
    public string TimestampText { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string Icon { get; init; } = "·";
    public string EventType { get; init; } = "";
    public string Summary { get; init; } = "";
    public string DetailText { get; init; } = "";
    public string Severity { get; init; } = "Info";
    public Brush SeverityBrush { get; init; } = Brushes.Gray;
    public Brush RowBackground { get; init; } = Brushes.Transparent;

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public static TimelineEntryVM FromProto(AuditLogEntry e, string agentName)
    {
        var ts = DateTime.TryParse(e.Timestamp, out var d) ? d.ToLocalTime() : DateTime.Now;
        var severity = e.Severity;
        var (icon, brush, rowBg) = GetVisuals(severity, e.Event);

        var summary = e.Event;
        if (!string.IsNullOrEmpty(e.Command))
            summary += $" — {e.Command}";
        if (e.ExitCode != 0)
            summary += $" (exit {e.ExitCode})";
        if (e.DurationMs > 0)
            summary += $" [{e.DurationMs}ms]";

        return new TimelineEntryVM
        {
            Timestamp     = ts,
            TimestampText = ts.ToString("HH:mm:ss"),
            AgentName     = agentName,
            Icon          = icon,
            EventType     = e.Event,
            Summary       = summary,
            DetailText    = e.Detail ?? "",
            Severity      = severity,
            SeverityBrush = brush,
            RowBackground = rowBg,
        };
    }

    public static TimelineEntryVM FromLiveEvent(ExecutionEvent evt, string agentName)
    {
        var ts = evt.Timestamp?.ToDateTime().ToLocalTime() ?? DateTime.Now;
        var eventType = evt.EventType.ToString().Replace("Event", "");
        var severity = evt.EventType switch
        {
            ExecutionEventType.EventFailed or ExecutionEventType.EventTerminated => "Error",
            ExecutionEventType.EventStderrLine => "Warning",
            ExecutionEventType.EventCompleted when evt.ExitCode != 0 => "Warning",
            _ => "Info",
        };
        var (icon, brush, rowBg) = GetVisuals(severity, eventType);

        var summary = eventType;
        if (!string.IsNullOrEmpty(evt.Command))
            summary += $" — {evt.Command}";
        if (!string.IsNullOrEmpty(evt.Detail))
            summary += $" — {evt.Detail}";

        return new TimelineEntryVM
        {
            Timestamp     = ts,
            TimestampText = ts.ToString("HH:mm:ss"),
            AgentName     = agentName,
            Icon          = icon,
            EventType     = eventType,
            Summary       = summary,
            DetailText    = evt.Detail ?? evt.ErrorMessage ?? "",
            Severity      = severity,
            SeverityBrush = brush,
            RowBackground = rowBg,
        };
    }

    private static (string Icon, Brush SevBrush, Brush RowBg) GetVisuals(string severity, string eventType)
    {
        // Event-specific icons
        var icon = eventType switch
        {
            _ when eventType.Contains("Command") && eventType.Contains("Start") => "?",
            _ when eventType.Contains("Command") && eventType.Contains("Complet") => "?",
            _ when eventType.Contains("Command") && eventType.Contains("Fail") => "?",
            _ when eventType.Contains("Command") && eventType.Contains("Terminat") => "?",
            _ when eventType.Contains("Command") && eventType.Contains("Receiv") => "?",
            _ when eventType.Contains("Command") && eventType.Contains("Reject") => "?",
            _ when eventType.Contains("Heartbeat") => "?",
            _ when eventType.Contains("Regist") => "?",
            _ when eventType.Contains("Controller") && eventType.Contains("Lost") => "?",
            _ when eventType.Contains("Controller") && eventType.Contains("Recover") => "?",
            _ when eventType.Contains("Agent") && eventType.Contains("Start") => "?",
            _ when eventType.Contains("Agent") && eventType.Contains("Stop") => "?",
            _ when eventType.Contains("Started") => "?",
            _ when eventType.Contains("Completed") => "?",
            _ when eventType.Contains("Failed") || eventType.Contains("Terminated") => "?",
            _ when eventType.Contains("Queued") => "?",
            _ when eventType.Contains("StateChanged") => "?",
            _ when eventType.Contains("Heartbeat") => "?",
            _ => "·",
        };

        Brush sevBrush;
        Brush rowBg;

        switch (severity.ToLowerInvariant())
        {
            case "error":
                sevBrush = new SolidColorBrush(Color.FromRgb(239, 83, 80));   // #EF5350
                rowBg = new SolidColorBrush(Color.FromArgb(30, 239, 83, 80));
                break;
            case "warning":
                sevBrush = new SolidColorBrush(Color.FromRgb(255, 167, 38));  // #FFA726
                rowBg = new SolidColorBrush(Color.FromArgb(20, 255, 167, 38));
                break;
            default: // info, success
                sevBrush = new SolidColorBrush(Color.FromRgb(102, 187, 106)); // #66BB6A
                rowBg = Brushes.Transparent;
                break;
        }

        sevBrush.Freeze();
        if (rowBg.CanFreeze) rowBg.Freeze();
        return (icon, sevBrush, rowBg);
    }
}
