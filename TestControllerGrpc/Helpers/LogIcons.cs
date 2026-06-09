using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Helpers;

/// <summary>
/// Centralized Unicode symbols for structured log output.
/// All symbols are chosen from the BMP (Basic Multilingual Plane)
/// so they render reliably in WPF TextBlock / console.
/// </summary>
public static class LogIcons
{
    // ?? Severity indicators ?????????????????????????????????????????
    public const string Success = "\u2714";   // ?
    public const string Error   = "\u2716";   // ?
    public const string Warning = "\u26A0";   // ?
    public const string Info    = "\u2139";   // ?

    // ?? Diagnostic / workflow ???????????????????????????????????????
    public const string Diagnose  = "\u2315"; // ? (telephone recorder / search — BMP safe)
    public const string Arrow     = "\u2192"; // ?
    public const string Heartbeat = "\u2764"; // ?

    /// <summary>Returns the appropriate icon for a diagnostic step result.</summary>
    public static string ForDiagnosticStep(bool passed, bool isFatal)
        => passed ? Success : isFatal ? Error : Warning;

    /// <summary>Returns the appropriate icon for a severity level.</summary>
    public static string ForSeverity(LogSeverity severity) => severity switch
    {
        LogSeverity.Success => Success,
        LogSeverity.Error   => Error,
        LogSeverity.Warning => Warning,
        _                   => Info,
    };
}
