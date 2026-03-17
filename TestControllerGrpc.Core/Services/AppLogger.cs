using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Structured application logger that writes to:
///   1. An in-memory ring buffer (for UI display and recent query)
///   2. Date-stamped log files on disk (e.g. controller_2025-01-15.log)
///
/// Thread-safe. Auto-rotates to a new file each day.
/// </summary>
public sealed class AppLogger : IAppLogger, IDisposable
{
    private readonly string _logDirectory;
    private readonly string _appName;
    private readonly List<AppLogEntry> _ringBuffer = new();
    private readonly object _lock = new();
    private readonly int _maxBuffer;
    private StreamWriter? _fileWriter;
    private string _currentDate = "";

    public event Action<AppLogEntry>? EntryAdded;

    public AppLogger(string appName, string logDirectory, int maxBuffer = 5000)
    {
        _appName = appName;
        _logDirectory = logDirectory;
        _maxBuffer = maxBuffer;
        Directory.CreateDirectory(logDirectory);
    }

    public void Log(LogLevel level, string category, string message, Exception? ex = null)
    {
        var entry = new AppLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            Exception = ex?.ToString(),
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
                    _fileWriter?.Dispose();
                    _currentDate = date;
                    var path = Path.Combine(_logDirectory, $"{_appName}_{date}.log");
                    _fileWriter = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
                }

                var line = $"{entry.Timestamp:HH:mm:ss.fff} [{entry.Level}] [{entry.Category}] {entry.Message}";
                if (entry.Exception is not null)
                    line += $"\n  EXCEPTION: {entry.Exception}";
                _fileWriter?.WriteLine(line);
            }
        }
        catch
        {
            // Don't crash on log failure
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
    }
}
