using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Represents a single entry in the execution log with severity level
/// for color-coded display (failures shown in red).
/// </summary>
public sealed partial class LogEntryViewModel : ObservableObject
{
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private LogSeverity _severity = LogSeverity.Info;
    [ObservableProperty] private string _component = "";

    // Fields for concurrent execution session tracking
    /// <summary>Short session ID (e.g., "a1b2c3") for filtering.</summary>
    [ObservableProperty] private string _sessionId = "";

    /// <summary>Agent name extracted from the message (e.g., "jvgr1").</summary>
    [ObservableProperty] private string _agentName = "";

    /// <summary>WatchItem tag this log entry belongs to.</summary>
    [ObservableProperty] private string _watchItemTag = "";

    /// <summary>Category for grouping: Action, Event, Initialize, Controller, Agent.</summary>
    [ObservableProperty] private string _category = "";

    /// <summary>True if this is a stdout/stderr line from an agent.</summary>
    public bool IsAgentOutput => Category.EndsWith(":stdout") || Category.EndsWith(":stderr");

    /// <summary>Full formatted text with session prefix for clipboard.</summary>
    public string FullText => string.IsNullOrEmpty(SessionId)
        ? $"[{Timestamp}] [{Severity}] [{Component}] {Message}"
        : $"[{Timestamp}] [{SessionId}] [{Severity}] [{Component}] {Message}";

    /// <summary>Display-friendly severity text.</summary>
    public string SeverityText => Severity switch
    {
        LogSeverity.Info => "Info",
        LogSeverity.Success => "Success",
        LogSeverity.Warning => "Warning",
        LogSeverity.Error => "Error",
        _ => "Info",
    };

    /// <summary>Color for the session ID badge in the log list.</summary>
    public string SessionColor => SessionId switch
    {
        "" => "#00000000",
        _ => SessionColorMap.GetColor(SessionId)
    };
}

/// <summary>
/// Assigns consistent colors to session IDs so each session
/// has a visually distinct color in the log.
/// </summary>
public static class SessionColorMap
{
    private static readonly string[] Colors =
    [
        "#3B82F6", // Blue
        "#10B981", // Green
        "#F59E0B", // Amber
        "#EF4444", // Red
        "#8B5CF6", // Purple
        "#06B6D4", // Cyan
        "#F97316", // Orange
        "#EC4899", // Pink
    ];

    private static readonly Dictionary<string, string> Map = new();
    private static int _nextIndex;

    public static string GetColor(string sessionId)
    {
        if (Map.TryGetValue(sessionId, out var color)) return color;
        color = Colors[_nextIndex % Colors.Length];
        Map[sessionId] = color;
        _nextIndex++;
        return color;
    }
}

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error
}
