namespace TestAgent.Diagnostics.Models;

/// <summary>Log severity, ordered so higher = more severe (used for the Severity filter floor).</summary>
public enum Severity
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// One parsed log line. The single shared record both views read (parse once, two views).
/// Free-form text logs are parsed tolerantly: RunId comes from the correlation id or the
/// execution session id; Agent / Action / Pipeline are best-effort extracted from the message.
/// </summary>
public sealed class LogRecord
{
    public DateTime Timestamp { get; init; }
    public Severity Severity { get; init; }

    /// <summary>Subsystem — the logger Category (HTTP, UI, Executor, Session, …).</summary>
    public string Component { get; init; } = "";

    /// <summary>Best-effort agent / target name extracted from the message ("" if unknown).</summary>
    public string Agent { get; set; } = "";

    /// <summary>One id per pipeline run: correlation id, or the execution session id.</summary>
    public string RunId { get; set; } = "";

    /// <summary>Pipeline / WatchItem name (from "[Session] Started &lt;id&gt; for &lt;pipeline&gt;").</summary>
    public string Pipeline { get; set; } = "";

    /// <summary>Step name (Install, Deploy, Execute, RunCommand, …) — best effort.</summary>
    public string Action { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>Exception text (first continuation line after EXCEPTION:), if any.</summary>
    public string? Exception { get; set; }

    /// <summary>Stack-trace continuation lines, if any.</summary>
    public string? StackTrace { get; set; }

    /// <summary>Source log file this record came from.</summary>
    public string SourceFile { get; init; } = "";

    public bool IsError => Severity == Severity.Error;
    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string SeverityText => Severity.ToString();
}
