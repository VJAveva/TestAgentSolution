using System.IO;
using System.Text;
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
        LogLevelFilter = "All";
        LogSearchText = "";
    }

    /// <summary>Applies tag/agent/severity/search filters to the execution log.</summary>
    private void ApplyLogFilter()
    {
        var hasTagFilter = !string.IsNullOrWhiteSpace(LogFilterTag);
        var hasAgentFilter = !string.IsNullOrWhiteSpace(LogFilterAgent);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(LogSearchText);
        var hasSeverityFilter = LogLevelFilter != "All";
        var anyFilter = hasTagFilter || hasAgentFilter || hasSearchFilter || hasSeverityFilter;

        // Capture filter values for the predicate closure
        var tagVal = LogFilterTag;
        var agentVal = LogFilterAgent;
        var searchVal = LogSearchText;
        var levelVal = LogLevelFilter;

        _logBuffer?.SetFilter(anyFilter
            ? entry => PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSearchFilter, hasSeverityFilter,
                                    tagVal, agentVal, searchVal, levelVal)
            : null);
        _logBuffer?.ReapplyFilter();
    }

    /// <summary>Pure filter predicate — no field access, fully parameterized for thread safety.</summary>
    private static bool PassesFilter(LogEntryViewModel entry,
        bool hasTagFilter, bool hasAgentFilter, bool hasSearchFilter, bool hasSeverityFilter,
        string tagVal, string agentVal, string searchVal, string levelVal)
    {
        var msg = entry.Message;

        if (hasTagFilter && !msg.Contains(tagVal, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasAgentFilter && !msg.Contains(agentVal, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasSearchFilter && !msg.Contains(searchVal, StringComparison.OrdinalIgnoreCase)
            && !entry.Timestamp.Contains(searchVal, StringComparison.OrdinalIgnoreCase))
            return false;
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
        return PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSearchFilter, hasSeverityFilter,
                            LogFilterTag, LogFilterAgent, LogSearchText, LogLevelFilter);
    }

    /// <summary>Copy all log entries to clipboard.</summary>
    [RelayCommand]
    private void CopyLog()
    {
        var sb = new StringBuilder();
        foreach (var e in LogEntries)
            sb.AppendLine($"[{e.Timestamp}] {e.Message}");
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    /// <summary>Copy only failed/error log entries to clipboard.</summary>
    [RelayCommand]
    private void CopyFailedLog()
    {
        var sb = new StringBuilder();
        foreach (var e in LogEntries.Where(e => e.Severity == LogSeverity.Error))
            sb.AppendLine($"[{e.Timestamp}] {e.Message}");
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    /// <summary>Clear all log entries.</summary>
    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        FilteredLogEntries.Clear();
        _logBuffer?.SetFilter(null);
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

    /// <summary>Export log entries to a text file.</summary>
    [RelayCommand]
    private void ExportLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text Files|*.txt|Log Files|*.log|All Files|*.*",
            FileName = $"ExecutionLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Title = "Export Execution Log"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        foreach (var e in LogEntries)
            sb.AppendLine($"[{e.Timestamp}] [{e.Severity}] {e.Message}");
        File.WriteAllText(dlg.FileName, sb.ToString());
        AddLog($"Log exported to {dlg.FileName}", LogSeverity.Success);
    }

    private void AddLog(string msg, LogSeverity severity = LogSeverity.Info)
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
            Severity = severity
        };

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
