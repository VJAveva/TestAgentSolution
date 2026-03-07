using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
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
        FilteredLogEntries.Clear();

        var hasTagFilter = !string.IsNullOrWhiteSpace(LogFilterTag);
        var hasAgentFilter = !string.IsNullOrWhiteSpace(LogFilterAgent);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(LogSearchText);
        var hasSeverityFilter = LogLevelFilter != "All";

        foreach (var entry in LogEntries)
        {
            if (PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSearchFilter, hasSeverityFilter))
                FilteredLogEntries.Add(entry);
        }
    }

    private bool PassesFilter(LogEntryViewModel entry,
        bool hasTagFilter, bool hasAgentFilter, bool hasSearchFilter, bool hasSeverityFilter)
    {
        var msg = entry.Message;

        if (hasTagFilter && !msg.Contains(LogFilterTag, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasAgentFilter && !msg.Contains(LogFilterAgent, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasSearchFilter && !msg.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase)
            && !entry.Timestamp.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasSeverityFilter)
        {
            var requiredSeverity = LogLevelFilter switch
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
    }

    /// <summary>Toggle the log panel collapsed/expanded state.</summary>
    [RelayCommand]
    private void ToggleLogCollapse()
    {
        IsLogCollapsed = !IsLogCollapsed;
    }

    /// <summary>Toggle pausing live log updates.</summary>
    [RelayCommand]
    private void ToggleLogPause()
    {
        IsLogPaused = !IsLogPaused;
        if (!IsLogPaused)
            ApplyLogFilter(); // refresh filtered entries when resuming
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
             || msg.Contains("?", StringComparison.Ordinal)
             || msg.Contains("?", StringComparison.Ordinal)
             || msg.StartsWith("[Action] X", StringComparison.Ordinal))
                severity = LogSeverity.Error;
            else if (msg.Contains("success", StringComparison.OrdinalIgnoreCase)
                  || msg.Contains("completed", StringComparison.OrdinalIgnoreCase)
                  || msg.Contains("?", StringComparison.Ordinal)
                  || msg.Contains("?", StringComparison.Ordinal))
                severity = LogSeverity.Success;
        }

        var entry = new LogEntryViewModel
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Message = msg,
            Severity = severity
        };

        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            LogEntries.Add(entry);
            while (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
            AddToFilteredLog(entry);
        }
        else
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Add(entry);
                while (LogEntries.Count > 5000) LogEntries.RemoveAt(0);
                AddToFilteredLog(entry);
            });
        }
    }

    private void AddToFilteredLog(LogEntryViewModel entry)
    {
        if (IsLogPaused) return;

        var hasTagFilter = !string.IsNullOrWhiteSpace(LogFilterTag);
        var hasAgentFilter = !string.IsNullOrWhiteSpace(LogFilterAgent);
        var hasSearchFilter = !string.IsNullOrWhiteSpace(LogSearchText);
        var hasSeverityFilter = LogLevelFilter != "All";

        if (PassesFilter(entry, hasTagFilter, hasAgentFilter, hasSearchFilter, hasSeverityFilter))
        {
            FilteredLogEntries.Add(entry);
            while (FilteredLogEntries.Count > 5000) FilteredLogEntries.RemoveAt(0);
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
