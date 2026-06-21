using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>Log entry model for the application logger.</summary>
public record AppLogEntry
{
    public long Sequence { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
    public string ThreadId { get; init; } = Environment.CurrentManagedThreadId.ToString();
    public string? CorrelationId { get; init; }
    public long ElapsedMs { get; init; }

    // ── Structured tracing fields (additive; null when not supplied) ──────
    /// <summary>Agent machine this entry pertains to (distinct from <see cref="Category"/>/component).</summary>
    public string? Agent { get; init; }
    /// <summary>Run/execution identifier grouping every entry of one execution end-to-end.</summary>
    public string? RunId { get; init; }
    /// <summary>Pipeline / WatchItem identity (e.g. resolved tag).</summary>
    public string? Pipeline { get; init; }
    /// <summary>Specific action identity within the pipeline.</summary>
    public string? Action { get; init; }
    /// <summary>Exception stack trace captured as a single field.</summary>
    public string? StackTrace { get; init; }
}

/// <summary>
/// Application-level structured logger that writes to an in-memory ring buffer
/// (for UI display) and to date-stamped log files on disk.
/// </summary>
public interface IAppLogger
{
    void Log(LogLevel level, string category, string message, Exception? ex = null);
    void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null);

    /// <summary>
    /// Structured log overload. Populates the distinct tracing fields
    /// (<paramref name="agent"/>, <paramref name="runId"/>, <paramref name="pipeline"/>,
    /// <paramref name="action"/>) so logs can be filtered/correlated without
    /// overloading <paramref name="category"/>. Exception stack trace is captured
    /// as a single field. All sinks (text + JSON) receive the same entry.
    /// </summary>
    void LogStructured(LogLevel level, string category, string message,
        string? agent = null, string? runId = null, string? pipeline = null,
        string? action = null, long elapsedMs = 0, Exception? ex = null);

    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);
    IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500);
    event Action<AppLogEntry>? EntryAdded;
}
