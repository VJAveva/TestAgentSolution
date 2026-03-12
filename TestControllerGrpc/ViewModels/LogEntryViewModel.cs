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

    /// <summary>Full formatted text for clipboard copy.</summary>
    public string FullText => $"[{Timestamp}] [{Severity}] [{Component}] {Message}";

    /// <summary>Display-friendly severity text.</summary>
    public string SeverityText => Severity switch
    {
        LogSeverity.Info => "Info",
        LogSeverity.Success => "Success",
        LogSeverity.Warning => "Warning",
        LogSeverity.Error => "Error",
        _ => "Info",
    };
}

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error
}
