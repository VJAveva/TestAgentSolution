using System.IO;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.Services;

/// <summary>Loads and parses one or more log files into the shared record set (stream-parsed).</summary>
public sealed class LogLoader
{
    private readonly LogParser _textParser = new();

    /// <summary>
    /// Parse all given files once into a single ordered list. Files are read line-by-line
    /// (File.ReadLines streams them) so the whole file is never held in memory at once.
    /// The parser is chosen per file: .jsonl → structured, *_crash.log → crash, else text.
    /// </summary>
    public IReadOnlyList<LogRecord> Load(IEnumerable<string> filePaths)
    {
        var records = new List<LogRecord>();
        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            var name = Path.GetFileName(path);
            try
            {
                foreach (var rec in ParseFile(path, name))
                    records.Add(rec);
            }
            catch (IOException)
            {
                // Skip a file that can't be read (locked / removed); other files still load.
            }
        }

        records.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return records;
    }

    private IEnumerable<LogRecord> ParseFile(string path, string name) =>
        LogFormatDetector.Detect(path) switch
        {
            LogFormat.Json => ParseJson(File.ReadLines(path), name),
            LogFormat.Crash => new CrashLogParser(path).Parse(File.ReadLines(path), name),
            _ => _textParser.Parse(File.ReadLines(path), name),
        };

    private static IEnumerable<LogRecord> ParseJson(IEnumerable<string> lines, string name)
    {
        foreach (var line in lines)
            if (JsonLogParser.TryParse(line, name, out var rec))
                yield return rec;
    }

    /// <summary>
    /// Discover the newest live-tailable files in a log directory: today's structured
    /// sink (preferred) or shared text log, the errors log, and any crash dumps. Used by
    /// "Go Live" so the user does not have to hand-pick the current day's files.
    /// </summary>
    public static IReadOnlyList<string> LatestLiveFiles(string directory)
    {
        var result = new List<string>();
        if (!Directory.Exists(directory)) return result;

        string? Newest(string pattern) => Directory
            .EnumerateFiles(directory, pattern)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;

        // Prefer the structured JSONL sink; fall back to the shared text log.
        var app = Newest("app_*.jsonl") ?? Newest("app_*.log");
        if (app is not null) result.Add(app);

        var errors = Newest("errors_*.log");
        if (errors is not null) result.Add(errors);

        // Crash dumps are append-only and low-volume — always follow them so a host
        // crash shows up live even when nothing else is being written.
        foreach (var crash in Directory.EnumerateFiles(directory, "*_crash.log"))
            result.Add(crash);

        return result;
    }
}
