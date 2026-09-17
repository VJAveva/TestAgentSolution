using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace TestAgentDisplay.Services;

/// <summary>
/// Minimal diagnostics log for the display app. Deliberately dependency-free: this project ships
/// self-contained and does NOT reference TestControllerGrpc.Core, so it cannot use IAppLogger.
/// </summary>
public interface IDisplayLog
{
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);

    /// <summary>Most recent entries, newest last. Bounded; safe to bind to.</summary>
    IReadOnlyList<string> Recent { get; }
}

/// <summary>
/// Writes to a daily file under %LOCALAPPDATA%\TestAgentDisplay\logs and keeps a bounded in-memory
/// tail. File writes are best-effort: a diagnostics tool must never crash because it could not log.
/// </summary>
public sealed class DisplayLog : IDisplayLog
{
    private const int MaxInMemory = 500;

    private readonly ConcurrentQueue<string> _recent = new();
    private readonly object _fileGate = new();
    private readonly string _logDirectory;

    public DisplayLog(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestAgentDisplay", "logs");
    }

    public IReadOnlyList<string> Recent => _recent.ToArray();

    public void Info(string category, string message) => Write("INFO", category, message, null);
    public void Warn(string category, string message) => Write("WARN", category, message, null);
    public void Error(string category, string message, Exception? ex = null) => Write("ERROR", category, message, ex);

    private void Write(string level, string category, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ")
            .Append(category).Append(" | ")
            .Append(message);

        if (ex is not null) line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        var text = line.ToString();

        _recent.Enqueue(text);
        while (_recent.Count > MaxInMemory) _recent.TryDequeue(out _);

        try
        {
            lock (_fileGate)
            {
                Directory.CreateDirectory(_logDirectory);
                var path = Path.Combine(_logDirectory, $"display-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, text + Environment.NewLine);
            }
        }
        catch
        {
            // Losing a log line must never take the UI down with it.
        }
    }
}
