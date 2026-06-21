using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.Services;

/// <summary>
/// Stateful parser for the host crash logs (<c>webapi_crash.log</c>,
/// <c>controller_crash.log</c>, <c>unknown_crash.log</c>, <c>agent_crash.log</c>).
///
/// These are NOT in the <c>[LVL]</c> layout the text logger uses, so the normal
/// <see cref="LogParser"/> ignores them. Two line shapes appear:
///   <list type="bullet">
///     <item><c>ts === host Startup ... PID=NNN ===</c>  → an Info "process start" marker.</item>
///     <item><c>ts [ExceptionType] message</c> + indented stack lines → an Error crash record.</item>
///   </list>
/// Surfacing these is the whole point of "trace error crashes": an unhandled
/// crash is exactly what never makes it into the structured pipeline logs.
/// </summary>
public sealed partial class CrashLogParser
{
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(?<rest>.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\[(?<type>[^\]]+)\]\s*(?<msg>.*)$")]
    private static partial Regex TypeRegex();

    private readonly string _host;
    private LogRecord? _current;

    /// <summary>The host name is derived from the file name prefix (webapi/controller/agent/unknown).</summary>
    public CrashLogParser(string sourceFile)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFile);
        var cut = name.IndexOf("_crash", StringComparison.OrdinalIgnoreCase);
        _host = cut > 0 ? name[..cut] : name;
    }

    public void Reset() => _current = null;

    public IEnumerable<LogRecord> Parse(IEnumerable<string> lines, string sourceFile)
    {
        Reset();
        foreach (var raw in lines)
            foreach (var rec in Feed(raw ?? "", sourceFile))
                yield return rec;
        foreach (var rec in Flush())
            yield return rec;
    }

    public IEnumerable<LogRecord> Feed(string line, string sourceFile)
    {
        var m = HeaderRegex().Match(line);
        if (!m.Success)
        {
            // Continuation: stack-trace / exception detail under the current crash.
            if (_current is not null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("at ", StringComparison.Ordinal)
                    || trimmed.StartsWith("--- ", StringComparison.Ordinal))
                    _current.StackTrace = string.IsNullOrEmpty(_current.StackTrace)
                        ? line : _current.StackTrace + "\n" + line;
                else if (trimmed.Length > 0)
                    _current.Exception = string.IsNullOrEmpty(_current.Exception)
                        ? trimmed : _current.Exception + "\n" + trimmed;
            }
            yield break;
        }

        if (_current is not null)
        {
            yield return _current;
            _current = null;
        }

        var ts = DateTime.ParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        var rest = m.Groups["rest"].Value.Trim();

        // "=== host Startup ... ===" — a lifecycle marker, not a failure.
        if (rest.StartsWith("===", StringComparison.Ordinal))
        {
            _current = new LogRecord
            {
                Timestamp = ts,
                Severity = Severity.Info,
                Component = "Process",
                Agent = _host,
                Action = "Startup",
                Message = rest.Trim('=', ' '),
                SourceFile = sourceFile,
            };
            yield break;
        }

        // "[ExceptionType] message" — an actual crash.
        var t = TypeRegex().Match(rest);
        var type = t.Success ? t.Groups["type"].Value.Trim() : "Crash";
        var msg = t.Success ? t.Groups["msg"].Value.Trim() : rest;

        _current = new LogRecord
        {
            Timestamp = ts,
            Severity = Severity.Error,
            Component = type,
            Agent = _host,
            Action = "Crash",
            Message = msg,
            SourceFile = sourceFile,
        };
    }

    public IEnumerable<LogRecord> Flush()
    {
        if (_current is not null)
        {
            yield return _current;
            _current = null;
        }
    }
}
