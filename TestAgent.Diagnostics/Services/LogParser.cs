using System.Globalization;
using System.Text.RegularExpressions;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.Services;

/// <summary>
/// Tolerant parser for the suite's free-form text logs (see AppLogger.FormatLine):
///   "yyyy-MM-dd HH:mm:ss.fff [LVL] [corr?] [Category] message (NNms)"
/// with exceptions emitted on indented continuation lines.
///
/// Because agent / action / pipeline are NOT first-class fields in the text logs,
/// they are extracted from the message; RunId comes from the correlation id, or from
/// the execution session id carried by "[Session] Started &lt;id&gt; for &lt;pipeline&gt;".
/// </summary>
public sealed partial class LogParser
{
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<lvl>INF|WRN|ERR|DBG)\](?<rest>.*)$")]
    private static partial Regex LineRegex();

    // A bracket group immediately after the level: correlation id (hex) or category.
    [GeneratedRegex(@"^\s*\[(?<tok>[^\]]+)\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{6,12}$")]
    private static partial Regex HexRegex();

    // "[Session] Started <id> for <pipeline>:<event>"
    [GeneratedRegex(@"Started\s+(?<id>[0-9a-fA-F]{8,})\s+for\s+(?<pipe>.+?)(?::(?<evt>[^:]*))?$")]
    private static partial Regex SessionStartRegex();

    // "[Session] Completed <id>: ..."
    [GeneratedRegex(@"Completed\s+(?<id>[0-9a-fA-F]{8,})")]
    private static partial Regex SessionEndRegex();

    // Known action verbs worth surfacing as a step name.
    [GeneratedRegex(@"\b(Initialize|Revert|Install|Configure|Deploy|Execute|Uninstall|Backup|Restore|Copy|Start|Stop|RunCommand)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionVerbRegex();

    // Agent hints: "[_AgentN]", "on <Name>", "(local)".
    [GeneratedRegex(@"\[_?Agent\d+\]|on\s+(?<a1>[A-Za-z][\w.-]*)|\((?<a2>local)\)")]
    private static partial Regex AgentRegex();

    /// <summary>Lazily parse a file's lines into records. Streams the file (does not buffer it whole).</summary>
    public IEnumerable<LogRecord> Parse(IEnumerable<string> lines, string sourceFile)
    {
        LogRecord? current = null;
        string activeRunId = "";
        string activePipeline = "";

        foreach (var raw in lines)
        {
            var line = raw ?? "";
            var m = LineRegex().Match(line);
            if (!m.Success)
            {
                // Continuation line — belongs to the previous record (exception / stack trace).
                if (current is not null)
                    AppendContinuation(current, line);
                continue;
            }

            if (current is not null)
                yield return current;

            var ts = DateTime.ParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture);
            var severity = m.Groups["lvl"].Value switch
            {
                "ERR" => Severity.Error,
                "WRN" => Severity.Warning,
                "DBG" => Severity.Debug,
                _ => Severity.Info,
            };

            var rest = m.Groups["rest"].Value;
            var (correlationId, component, message) = SplitHeader(rest);

            // Track the active execution session so action/detail lines inherit its run id + pipeline.
            var subTag = LeadingTag(message);
            if (string.Equals(subTag, "Session", StringComparison.OrdinalIgnoreCase))
            {
                var body = StripLeadingTag(message);
                var start = SessionStartRegex().Match(body);
                if (start.Success)
                {
                    activeRunId = start.Groups["id"].Value;
                    activePipeline = start.Groups["pipe"].Value.Trim();
                }
                else if (SessionEndRegex().IsMatch(body))
                {
                    // keep run id for the Completed line itself, then clear afterwards.
                }
            }

            var runId = !string.IsNullOrEmpty(correlationId) ? correlationId : activeRunId;
            var component2 = string.IsNullOrEmpty(subTag) ? component : subTag;

            current = new LogRecord
            {
                Timestamp = ts,
                Severity = severity,
                Component = component2,
                RunId = runId,
                Pipeline = activePipeline,
                Action = DeriveAction(message, component2),
                Agent = DeriveAgent(message),
                Message = message,
                SourceFile = sourceFile,
            };

            if (string.Equals(subTag, "Session", StringComparison.OrdinalIgnoreCase)
                && SessionEndRegex().IsMatch(message))
            {
                activeRunId = "";
                activePipeline = "";
            }
        }

        if (current is not null)
            yield return current;
    }

    /// <summary>Split "[corr?] [Category] message" — corr present only when the first bracket is hex.</summary>
    private static (string corr, string component, string message) SplitHeader(string rest)
    {
        var corr = "";
        var component = "";
        var cursor = rest;

        var b1 = BracketRegex().Match(cursor);
        if (b1.Success)
        {
            var tok = b1.Groups["tok"].Value.Trim();
            if (HexRegex().IsMatch(tok))
            {
                corr = tok;
                cursor = cursor[b1.Length..];
                var b2 = BracketRegex().Match(cursor);
                if (b2.Success)
                {
                    component = b2.Groups["tok"].Value.Trim();
                    cursor = cursor[b2.Length..];
                }
            }
            else
            {
                component = tok;
                cursor = cursor[b1.Length..];
            }
        }

        return (corr, component, cursor.TrimStart());
    }

    private static string LeadingTag(string message)
    {
        var b = BracketRegex().Match(message);
        return b.Success ? b.Groups["tok"].Value.Trim() : "";
    }

    private static string StripLeadingTag(string message)
    {
        var b = BracketRegex().Match(message);
        return b.Success ? message[b.Length..].TrimStart() : message;
    }

    private static string DeriveAction(string message, string component)
    {
        var v = ActionVerbRegex().Match(message);
        if (v.Success)
        {
            var s = v.Value;
            return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
        }
        // Fall back to the sub-tag / component as the step label.
        return component;
    }

    private static string DeriveAgent(string message)
    {
        var m = AgentRegex().Match(message);
        if (!m.Success) return "";
        if (m.Groups["a1"].Success) return m.Groups["a1"].Value;
        if (m.Groups["a2"].Success) return "local";
        // "[_AgentN]" form — return the token without brackets.
        return m.Value.Trim('[', ']');
    }

    private static void AppendContinuation(LogRecord rec, string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("EXCEPTION:", StringComparison.OrdinalIgnoreCase))
        {
            var ex = trimmed["EXCEPTION:".Length..].TrimStart();
            rec.Exception = string.IsNullOrEmpty(rec.Exception) ? ex : rec.Exception + "\n" + ex;
        }
        else if (trimmed.StartsWith("at ", StringComparison.Ordinal)
                 || trimmed.StartsWith("--- ", StringComparison.Ordinal))
        {
            rec.StackTrace = string.IsNullOrEmpty(rec.StackTrace) ? line : rec.StackTrace + "\n" + line;
        }
        else
        {
            // Any other continuation text — attach to the exception block.
            rec.Exception = string.IsNullOrEmpty(rec.Exception) ? trimmed : rec.Exception + "\n" + trimmed;
        }
    }
}
