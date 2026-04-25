using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Structured application logger that writes to:
///   1. An in-memory ring buffer (for UI display and recent query)
///   2. Component-specific daily rolling file (e.g. controller_2026-04-25.log)
///   3. Shared daily rolling file (app_2026-04-25.log) — all components in one place
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
    private StreamWriter? _componentWriter;
    private StreamWriter? _sharedWriter;
    private StreamWriter? _errorsWriter;
    private string _currentDate = "";
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

    public AppLogger(string appName, string logDirectory, int maxBuffer = 5000)
    {
        _appName = appName;
        _logDirectory = logDirectory;
        _maxBuffer = maxBuffer;
        Directory.CreateDirectory(logDirectory);
    }

    public void Log(LogLevel level, string category, string message, Exception? ex = null)
        => Log(level, category, message, correlationId: null, elapsedMs: 0, ex);

    public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null)
    {
        var seq = Interlocked.Increment(ref _globalSequence);
        var entry = new AppLogEntry
        {
            Sequence = seq,
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            Exception = ex?.ToString(),
            CorrelationId = correlationId,
            ElapsedMs = elapsedMs,
        };

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

                // 2. Shared file (app_2026-04-25.log) — all components
                _sharedWriter?.WriteLine(line);

                // 3. Errors-only file
                if (entry.Level >= LogLevel.Error)
                    _errorsWriter?.WriteLine(line);
            }
        }
        catch
        {
            // Don't crash on log failure
        }
    }

    private void RotateFiles(string date)
    {
        _componentWriter?.Dispose();
        _sharedWriter?.Dispose();
        _errorsWriter?.Dispose();
        _currentDate = date;

        _componentWriter = OpenSharedWriter($"{_appName}_{date}.log");
        _sharedWriter = OpenSharedWriter($"app_{date}.log");
        _errorsWriter = OpenSharedWriter($"errors_{date}.log");
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

    public void Dispose()
    {
        lock (_lock)
        {
            _componentWriter?.Dispose();
            _sharedWriter?.Dispose();
            _errorsWriter?.Dispose();
            _componentWriter = null;
            _sharedWriter = null;
            _errorsWriter = null;
        }
    }
}
