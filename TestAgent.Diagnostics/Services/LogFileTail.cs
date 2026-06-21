using System.IO;
using System.Text;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.Services;

/// <summary>The on-disk shape of a log file, which selects the parser.</summary>
public enum LogFormat
{
    /// <summary>Free-form "ts [LVL] [corr] [Cat] msg" lines (app_*.log, controller_*.log, errors_*.log).</summary>
    Text,
    /// <summary>One structured JSON object per line (app_*.jsonl) — first-class fields.</summary>
    Json,
    /// <summary>Host crash dumps (*_crash.log) — startup markers + unhandled exceptions.</summary>
    Crash,
}

/// <summary>Static helpers for classifying a log file by name.</summary>
public static class LogFormatDetector
{
    public static LogFormat Detect(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return LogFormat.Json;
        if (name.Contains("_crash", StringComparison.OrdinalIgnoreCase)) return LogFormat.Crash;
        return LogFormat.Text;
    }
}

/// <summary>
/// Follows a single log file: remembers the byte offset already consumed and, on each
/// <see cref="ReadNew"/>, returns only the records parsed from newly-appended content.
/// Handles trailing partial lines (held until their newline arrives) and file truncation
/// / daily rollover (offset resets when the file shrinks). One tail per source file.
/// </summary>
public sealed class LogFileTail
{
    private readonly string _path;
    private readonly string _name;
    private readonly LogFormat _format;
    private readonly LogParser _textParser;
    private readonly CrashLogParser? _crashParser;

    private long _offset;
    private string _pending = "";

    public LogFileTail(string path)
    {
        _path = path;
        _name = System.IO.Path.GetFileName(path);
        _format = LogFormatDetector.Detect(path);
        _textParser = new LogParser();
        _crashParser = _format == LogFormat.Crash ? new CrashLogParser(path) : null;
    }

    public string Path => _path;

    /// <summary>
    /// Skip everything currently in the file so only future appends are returned. Use after
    /// an initial bulk load to avoid re-emitting records that were already parsed.
    /// </summary>
    public void SeekToEnd()
    {
        try
        {
            var info = new FileInfo(_path);
            _offset = info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            _offset = 0;
        }
        _pending = "";
    }

    /// <summary>Read and parse everything appended since the last call (or since construction).</summary>
    public IReadOnlyList<LogRecord> ReadNew()
    {
        var produced = new List<LogRecord>();
        if (!File.Exists(_path)) return produced;

        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // Daily rollover / truncation: the file got smaller, so restart from the top.
            if (fs.Length < _offset)
            {
                _offset = 0;
                _pending = "";
                _textParser.Reset();
                _crashParser?.Reset();
            }

            if (fs.Length == _offset) return produced;

            fs.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var chunk = reader.ReadToEnd();
            _offset = fs.Length;

            var text = _pending + chunk;
            var lines = text.Split('\n');

            // The last element has no trailing newline yet — hold it until it completes.
            _pending = lines[^1];
            for (var i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].TrimEnd('\r');
                foreach (var rec in FeedLine(line))
                    produced.Add(rec);
            }
        }
        catch (IOException)
        {
            // Locked momentarily by the writer — try again on the next tick.
        }

        return produced;
    }

    private IEnumerable<LogRecord> FeedLine(string line) => _format switch
    {
        LogFormat.Json => JsonLogParser.TryParse(line, _name, out var rec)
            ? new[] { rec }
            : Array.Empty<LogRecord>(),
        LogFormat.Crash => _crashParser!.Feed(line, _name),
        _ => _textParser.Feed(line, _name),
    };
}
