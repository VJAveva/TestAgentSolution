using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Structured application logger that writes to:
///   1. An in-memory ring buffer (for UI display and recent query)
///   2. Component-specific daily rolling file (e.g. controller_2026-04-25.log)
///   3. Shared daily rolling file (app_2026-04-25.log) � all components in one place
///   4. Errors-only daily rolling file (errors_2026-04-25.log)
///
/// All files land in the same directory so a single folder contains
/// the full picture regardless of which host wrote the entry.
///
/// Thread-safe. Auto-rotates to a new file each day.
/// Supports correlation IDs for end-to-end request tracing.
/// </summary>
public sealed class AppLogger : IAppLogger, IDisposable
{
    private readonly string _logDirectory;
    private readonly string _appName;
    private readonly List<AppLogEntry> _ringBuffer = new();
    private readonly object _lock = new();
    private readonly int _maxBuffer;
    private readonly long _maxFileSizeBytes;
    private StreamWriter? _componentWriter;
    private StreamWriter? _sharedWriter;
    private StreamWriter? _errorsWriter;
    private StreamWriter? _jsonWriter;
    private string _currentDate = "";
    private int _componentRollover;
    private int _sharedRollover;
    private int _errorsRollover;
    private int _jsonRollover;
    private static long _globalSequence;

    /// <summary>
    /// Default shared log directory. All hosts (WPF, WebApi, Agent) should
    /// point here so logs are co-located for debugging.
    /// Override via appsettings "Logging:LogDirectory".
    /// </summary>
    public static string DefaultLogDirectory { get; set; } = @"C:\TestControllerService\Logs";

    /// <summary>The resolved log directory this instance writes to.</summary>
    public string LogDirectory => _logDirectory;

    public event Action<AppLogEntry>? EntryAdded;

    public AppLogger(string appName, string logDirectory, int maxBuffer = 5000, long maxFileSizeBytes = 50 * 1024 * 1024)
    {
        _appName = appName;
        _logDirectory = logDirectory;
        _maxBuffer = maxBuffer;
        _maxFileSizeBytes = maxFileSizeBytes;
        Directory.CreateDirectory(logDirectory);
    }

    public void Log(LogLevel level, string category, string message, Exception? ex = null)
        => Log(level, category, message, correlationId: null, elapsedMs: 0, ex);

    public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null)
    {
        var seq = Interlocked.Increment(ref _globalSequence);
        var redactedMessage = SecurityRedactor.Redact(message) ?? string.Empty;
        var redactedException = SecurityRedactor.Redact(ex?.ToString());
        var entry = new AppLogEntry
        {
            Sequence = seq,
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = redactedMessage,
            Exception = redactedException,
            CorrelationId = correlationId,
            RunId = correlationId,
            ElapsedMs = elapsedMs,
            StackTrace = ex?.StackTrace,
        };

        Emit(entry);
    }

    public void LogStructured(LogLevel level, string category, string message,
        string? agent = null, string? runId = null, string? pipeline = null,
        string? action = null, long elapsedMs = 0, Exception? ex = null)
    {
        var seq = Interlocked.Increment(ref _globalSequence);
        var redactedMessage = SecurityRedactor.Redact(message) ?? string.Empty;
        var redactedException = SecurityRedactor.Redact(ex?.ToString());
        var entry = new AppLogEntry
        {
            Sequence = seq,
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = redactedMessage,
            Exception = redactedException,
            CorrelationId = runId,
            RunId = runId,
            ElapsedMs = elapsedMs,
            Agent = agent,
            Pipeline = pipeline,
            Action = action,
            StackTrace = ex?.StackTrace,
        };

        Emit(entry);
    }

    private void Emit(AppLogEntry entry)
    {
        lock (_lock)
        {
            _ringBuffer.Add(entry);
            if (_ringBuffer.Count > _maxBuffer)
                _ringBuffer.RemoveAt(0);
        }

        WriteToFile(entry);
        EntryAdded?.Invoke(entry);
    }

    public void Info(string category, string message) =>
        Log(LogLevel.Information, category, message);

    public void Warn(string category, string message) =>
        Log(LogLevel.Warning, category, message);

    public void Error(string category, string message, Exception? ex = null) =>
        Log(LogLevel.Error, category, message, ex);

    public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500)
    {
        lock (_lock)
        {
            return _ringBuffer.TakeLast(count).ToList();
        }
    }

    private void WriteToFile(AppLogEntry entry)
    {
        try
        {
            var date = entry.Timestamp.ToString("yyyy-MM-dd");
            lock (_lock)
            {
                if (date != _currentDate)
                {
                    RotateFiles(date);
                }

                var line = FormatLine(entry);

                // 1. Component-specific file (e.g. controller_2026-04-25.log)
                _componentWriter?.WriteLine(line);
                CheckSizeRollover(ref _componentWriter, $"{_appName}_{_currentDate}", ref _componentRollover);

                // 2. Shared file (app_2026-04-25.log) — all components
                _sharedWriter?.WriteLine(line);
                CheckSizeRollover(ref _sharedWriter, $"app_{_currentDate}", ref _sharedRollover);

                // 3. Errors-only file
                if (entry.Level >= LogLevel.Error)
                {
                    _errorsWriter?.WriteLine(line);
                    CheckSizeRollover(ref _errorsWriter, $"errors_{_currentDate}", ref _errorsRollover);
                }

                // 4. Structured JSON-lines file (machine-readable; one object per line)
                _jsonWriter?.WriteLine(BuildJson(entry));
                CheckSizeRollover(ref _jsonWriter, $"app_{_currentDate}", ref _jsonRollover, ".jsonl");
            }
        }
        catch
        {
            // Don't crash on log failure
        }
    }

    private void CheckSizeRollover(ref StreamWriter? writer, string baseName, ref int rolloverCount, string extension = ".log")
    {
        if (writer is null) return;
        try
        {
            if (writer.BaseStream.Length >= _maxFileSizeBytes)
            {
                writer.Dispose();
                rolloverCount++;
                writer = OpenSharedWriter($"{baseName}.{rolloverCount}{extension}");
            }
        }
        catch
        {
            // Ignore size check failures — will retry next write
        }
    }

    private void RotateFiles(string date)
    {
        _componentWriter?.Dispose();
        _sharedWriter?.Dispose();
        _errorsWriter?.Dispose();
        _jsonWriter?.Dispose();
        _currentDate = date;
        _componentRollover = 0;
        _sharedRollover = 0;
        _errorsRollover = 0;
        _jsonRollover = 0;

        _componentWriter = OpenSharedWriter($"{_appName}_{date}.log");
        _sharedWriter = OpenSharedWriter($"app_{date}.log");
        _errorsWriter = OpenSharedWriter($"errors_{date}.log");
        _jsonWriter = OpenSharedWriter($"app_{date}.jsonl");
    }

    private StreamWriter OpenSharedWriter(string fileName)
    {
        var path = Path.Combine(_logDirectory, fileName);
        // FileShare.ReadWrite allows other processes (WPF + WebApi) to write to the same file,
        // and allows the health endpoint to read while we're writing.
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
    }

    private static string FormatLine(AppLogEntry entry)
    {
        var levelTag = entry.Level switch
        {
            LogLevel.Error => "ERR",
            LogLevel.Warning => "WRN",
            LogLevel.Debug => "DBG",
            _ => "INF",
        };
        var corr = entry.CorrelationId is not null ? $" [{entry.CorrelationId}]" : "";
        var elapsed = entry.ElapsedMs > 0 ? $" ({entry.ElapsedMs}ms)" : "";
        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{levelTag}]{corr} [{entry.Category}] {entry.Message}{elapsed}";
        if (entry.Exception is not null)
            line += $"\n  EXCEPTION: {entry.Exception}";
        return line;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serializes one entry to a single-line JSON object for the .jsonl sink.
    /// Emits the full ISO-8601 timestamp and the distinct structured fields so
    /// the file is queryable without parsing the human-readable layout.
    /// </summary>
    private static string BuildJson(AppLogEntry entry)
    {
        var level = entry.Level switch
        {
            LogLevel.Error => "Error",
            LogLevel.Warning => "Warning",
            LogLevel.Debug => "Debug",
            _ => "Info",
        };
        var obj = new Dictionary<string, object?>
        {
            ["timestamp"] = entry.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["seq"] = entry.Sequence,
            ["level"] = level,
            ["component"] = entry.Category,
            ["agent"] = entry.Agent,
            ["runId"] = entry.RunId,
            ["pipeline"] = entry.Pipeline,
            ["action"] = entry.Action,
            ["thread"] = entry.ThreadId,
            ["elapsedMs"] = entry.ElapsedMs > 0 ? entry.ElapsedMs : null,
            ["message"] = entry.Message,
            ["exception"] = entry.Exception,
            ["stackTrace"] = entry.StackTrace,
        };
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(obj, JsonOptions);
        }
        catch
        {
            return $"{{\"timestamp\":\"{entry.Timestamp:O}\",\"level\":\"{level}\",\"message\":\"(serialization failed)\"}}";
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _componentWriter?.Dispose();
            _sharedWriter?.Dispose();
            _errorsWriter?.Dispose();
            _jsonWriter?.Dispose();
            _componentWriter = null;
            _sharedWriter = null;
            _errorsWriter = null;
            _jsonWriter = null;
        }
    }
}
