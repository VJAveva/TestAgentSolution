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
}

/// <summary>
/// Application-level structured logger that writes to an in-memory ring buffer
/// (for UI display) and to date-stamped log files on disk.
/// </summary>
public interface IAppLogger
{
    void Log(LogLevel level, string category, string message, Exception? ex = null);
    void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null);
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);
    IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500);
    event Action<AppLogEntry>? EntryAdded;
}
