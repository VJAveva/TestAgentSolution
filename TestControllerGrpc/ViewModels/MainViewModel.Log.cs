using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Helpers;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Log entries, filtering, copy, clear, export ?????????????????????
public sealed partial class MainViewModel
{
    /// <summary>Clear log filters.</summary>
    [RelayCommand]
    private void ClearLogFilter()
    {
        LogFilterTag = "";
        LogFilterAgent = "";
        LogFilterSession = "";
        LogLevelFilter = "All";
        LogSearchText = "";
        IsRegexSearch = false;
        ShowErrorsOnly = false;
    }

    private void RebuildSearchRegex()
    {
        _searchRegex = null;
        if (IsRegexSearch && !string.IsNullOrWhiteSpace(LogSearchText))
        {
            try
            {
                _searchRegex = new Regex(
                    LogSearchText,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch
            {
                // Invalid regex — fall back to plain text
                _searchRegex = null;
            }
        }
        ApplyLogFilter();
    }

    /// <summary>Applies tag/agent/session/severity/search filters to the execution log.</summary>
    private void ApplyLogFilter()
    {
        var hasTagFilter = !string.IsNullOrWhiteSpace(LogFilterTag);
        var hasAgentFilter = !string.IsNullOrWhiteSpace(LogFilterAgent);
        var hasSessionFilter = !string.IsNullOrWhiteSpace(LogFilterSession);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(LogSearchText);
        var hasSeverityFilter = LogLevelFilter != "All";
        var errorsOnly = ShowErrorsOnly;
        var regexSearch = IsRegexSearch;
        var searchRegex = _searchRegex;
        var anyFilter = hasTagFilter || hasAgentFilter || hasSessionFilter
                     || hasSearchFilter || hasSeverityFilter || errorsOnly;

        // Capture filter values for the predicate closure
        var tagVal = LogFilterTag;
        var agentVal = LogFilterAgent;
        var sessionVal = LogFilterSession;
        var searchVal = LogSearchText;
        var levelVal = LogLevelFilter;

        _logBuffer?.SetFilter(anyFilter
            ? entry => PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSessionFilter,
                                    hasSearchFilter, hasSeverityFilter, errorsOnly, regexSearch, searchRegex,
                                    tagVal, agentVal, sessionVal, searchVal, levelVal)
            : null);
        _logBuffer?.ReapplyFilter();

        // Update search match count
        if (hasSearchFilter)
            SearchMatchCount = FilteredLogEntries.Count;
        else
            SearchMatchCount = 0;
    }

    /// <summary>Pure filter predicate — no field access, fully parameterized for thread safety.</summary>
    private static bool PassesFilter(LogEntryViewModel entry,
        bool hasTagFilter, bool hasAgentFilter, bool hasSessionFilter,
        bool hasSearchFilter, bool hasSeverityFilter,
        bool errorsOnly, bool regexSearch, Regex? searchRegex,
        string tagVal, string agentVal, string sessionVal, string searchVal, string levelVal)
    {
        // Quick error-only toggle
        if (errorsOnly && entry.Severity != LogSeverity.Error)
            return false;

        // Session filter
        if (hasSessionFilter && !string.Equals(entry.SessionId, sessionVal, StringComparison.OrdinalIgnoreCase))
            return false;

        // Tag filter (check dedicated field first, then message text)
        if (hasTagFilter)
        {
            var matchTag = !string.IsNullOrEmpty(entry.WatchItemTag)
                ? entry.WatchItemTag.Contains(tagVal, StringComparison.OrdinalIgnoreCase)
                : entry.Message.Contains(tagVal, StringComparison.OrdinalIgnoreCase);
            if (!matchTag) return false;
        }

        // Agent filter (check dedicated field first, then message text)
        if (hasAgentFilter)
        {
            var matchAgent = !string.IsNullOrEmpty(entry.AgentName)
                ? entry.AgentName.Contains(agentVal, StringComparison.OrdinalIgnoreCase)
                : entry.Message.Contains(agentVal, StringComparison.OrdinalIgnoreCase);
            if (!matchAgent) return false;
        }

        // Search (regex or plain text)
        if (hasSearchFilter)
        {
            if (regexSearch && searchRegex is not null)
            {
                if (!searchRegex.IsMatch(entry.Message) &&
                    !searchRegex.IsMatch(entry.Timestamp))
                    return false;
            }
            else
            {
                if (!entry.Message.Contains(searchVal, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Timestamp.Contains(searchVal, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        // Severity filter
        if (hasSeverityFilter)
        {
            var requiredSeverity = levelVal switch
            {
                "Info" => LogSeverity.Info,
                "Success" => LogSeverity.Success,
                "Warning" => LogSeverity.Warning,
                "Error" => LogSeverity.Error,
                _ => (LogSeverity?)null
            };
            if (requiredSeverity.HasValue && entry.Severity != requiredSeverity.Value)
                return false;
        }

        return true;
    }

    /// <summary>Overload used by AddToFilteredLog for the live path.</summary>
    private bool PassesFilter(LogEntryViewModel entry,
        bool hasTagFilter, bool hasAgentFilter, bool hasSearchFilter, bool hasSeverityFilter)
    {
        return PassesFilter(entry, hasTagFilter, hasAgentFilter,
            !string.IsNullOrWhiteSpace(LogFilterSession),
            hasSearchFilter, hasSeverityFilter,
            ShowErrorsOnly, IsRegexSearch, _searchRegex,
            LogFilterTag, LogFilterAgent, LogFilterSession, LogSearchText, LogLevelFilter);
    }

    /// <summary>Copy all log entries to clipboard.</summary>
    [RelayCommand]
    private void CopyLog()
    {
        var sb = new StringBuilder();
        foreach (var e in LogEntries)
            sb.AppendLine(e.FullText);
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    /// <summary>Copy only failed/error log entries to clipboard.</summary>
    [RelayCommand]
    private void CopyFailedLog()
    {
        var lines = LogEntries
            .Where(e => e.Severity == LogSeverity.Error)
            .Select(e => e.FullText);
        var text = string.Join(Environment.NewLine, lines);
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    /// <summary>Clear all log entries.</summary>
    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        FilteredLogEntries.Clear();
        _logBuffer?.SetFilter(null);
        LogErrorCount = 0;
        LogWarningCount = 0;
        SearchMatchCount = 0;
        AvailableSessionIds.Clear();
        AvailableSessionIds.Add("");
    }

    /// <summary>Toggle the log panel collapsed/expanded state.</summary>
    [RelayCommand]
    private void ToggleLogCollapse()
    {
        IsLogCollapsed = !IsLogCollapsed;
    }

    /// <summary>Toggle log pause on/off.</summary>
    [RelayCommand]
    private void ToggleLogPause()
    {
        IsLogPaused = !IsLogPaused;
    }

    /// <summary>Focus the log search box (Ctrl+L shortcut).</summary>
    [RelayCommand]
    private void FocusLogSearch()
    {
        FocusLogSearchRequested?.Invoke();
    }

    /// <summary>Raised when Ctrl+L is pressed to focus log search.</summary>
    public event Action? FocusLogSearchRequested;

    /// <summary>Toggle pin/auto-hide for the execution log pane.</summary>
    [RelayCommand]
    private void ToggleLogPanePin()
    {
        IsLogPanePinned = !IsLogPanePinned;
        if (IsLogPanePinned)
        {
            // Restore docked state
            IsLogCollapsed = false;
        }
    }

    /// <summary>Hide the log pane completely (restore via TOOLS ribbon).</summary>
    [RelayCommand]
    private void HideLogPane()
    {
        IsLogPanePinned = false;
    }

    /// <summary>Show and pin the log pane (restore from hidden state).</summary>
    [RelayCommand]
    private void ShowLogPane()
    {
        IsLogPanePinned = true;
        IsLogCollapsed = false;
    }

    // ?? Agent pane dock commands ?????????????????????????????????????

    /// <summary>Toggle pin/auto-hide for the agent pane.</summary>
    [RelayCommand]
    private void ToggleAgentPanePin()
    {
        IsAgentPanePinned = !IsAgentPanePinned;
    }

    /// <summary>Hide the agent pane completely (restore via ribbon checkbox).</summary>
    [RelayCommand]
    private void HideAgentPane()
    {
        IsAgentPanePinned = false;
    }

    /// <summary>Show and pin the agent pane.</summary>
    [RelayCommand]
    private void ShowAgentPane()
    {
        IsAgentPanePinned = true;
    }

    /// <summary>Export log entries to a file (text, log, or CSV).</summary>
    [RelayCommand]
    private void ExportLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv",
            FileName = $"ExecutionLog_{DateTime.Now:yyyyMMdd_HHmmss}",
            Title = "Export Execution Log"
        };
        if (dlg.ShowDialog() != true) return;

        var ext = Path.GetExtension(dlg.FileName).ToLower();
        IEnumerable<string> lines;

        if (ext == ".csv")
        {
            lines = new[] { "Timestamp,Session,Severity,Agent,WatchItem,Message" }
                .Concat(LogEntries.Select(e =>
                    $"\"{e.Timestamp}\",\"{e.SessionId}\",\"{e.SeverityText}\",\"{e.AgentName}\",\"{e.WatchItemTag}\",\"{e.Message.Replace("\"", "\"\"")}\""
                ));
        }
        else
        {
            lines = LogEntries.Select(e => e.FullText);
        }

        File.WriteAllLines(dlg.FileName, lines);
        AddLog($"Log exported to {dlg.FileName}", LogSeverity.Success);
    }

    private void AddLog(string msg, LogSeverity severity = LogSeverity.Info,
        string sessionId = "", string agentName = "", string watchItemTag = "")
    {
        // Auto-detect severity from message content when using default
        if (severity == LogSeverity.Info)
        {
            if (msg.Contains("error", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("failed", StringComparison.OrdinalIgnoreCase)
             || msg.Contains(LogIcons.Error, StringComparison.Ordinal)
             || msg.StartsWith("[Action] X", StringComparison.Ordinal))
                severity = LogSeverity.Error;
            else if (msg.Contains("success", StringComparison.OrdinalIgnoreCase)
                  || msg.Contains("completed", StringComparison.OrdinalIgnoreCase)
                  || msg.Contains(LogIcons.Success, StringComparison.Ordinal))
                severity = LogSeverity.Success;
        }

        // Write to structured file logger
        var logLevel = severity switch
        {
            LogSeverity.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            LogSeverity.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            _ => Microsoft.Extensions.Logging.LogLevel.Information,
        };
        _appLogger.Log(logLevel, "UI", msg);

        var entry = new LogEntryViewModel
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Message = msg,
            Severity = severity,
            SessionId = sessionId,
            AgentName = agentName,
            WatchItemTag = watchItemTag,
        };

        // Track error/warning counts and session IDs on the UI thread
        void TrackCounts()
        {
            if (entry.Severity == LogSeverity.Error) LogErrorCount++;
            if (entry.Severity == LogSeverity.Warning) LogWarningCount++;
            if (!string.IsNullOrEmpty(sessionId) && !AvailableSessionIds.Contains(sessionId))
                AvailableSessionIds.Add(sessionId);
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            TrackCounts();
        else
            Application.Current?.Dispatcher.InvokeAsync(TrackCounts);

        // Enqueue into the high-performance buffer (lock-free, any thread).
        // The buffer drains in batches on the UI thread every 100ms.
        if (_logBuffer is not null)
        {
            _logBuffer.IsPaused = IsLogPaused;
            _logBuffer.Enqueue(entry);
        }
    }

    /// <summary>
    /// Scrolls the execution log to the last error entry and highlights it.
    /// Ensures the error is visible even if auto-scroll is disabled.
    /// </summary>
    private void ScrollLogToLastError()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var lastError = FilteredLogEntries.LastOrDefault(e => e.Severity == LogSeverity.Error);
            lastError ??= LogEntries.LastOrDefault(e => e.Severity == LogSeverity.Error);

            if (lastError is not null)
            {
                // Signal the view to scroll — uses the existing auto-scroll mechanism
                // by temporarily ensuring the item is the last visible entry
                ScrollToLogEntry?.Invoke(lastError);
            }
        });
    }

    /// <summary>Raised when the log should scroll to a specific entry.</summary>
    public event Action<LogEntryViewModel>? ScrollToLogEntry;
}
