using System.Globalization;
using System.Text.Json;
using TestAgent.Diagnostics.Models;

namespace TestAgent.Diagnostics.Services;

/// <summary>
/// Parser for the structured <c>app_{date}.jsonl</c> sink (one JSON object per line,
/// see AppLogger.BuildJson). Unlike the free-form text logs, every field
/// (component, agent, runId, pipeline, action, exception, stackTrace) is
/// first-class here, so records are accurate instead of best-effort extracted.
/// Each line is a complete record, so the parser is stateless and trivially tailable.
/// </summary>
public static class JsonLogParser
{
    /// <summary>
    /// Try to parse one JSONL line into a <see cref="LogRecord"/>.
    /// Returns false for blank lines or malformed JSON (the tailer skips those).
    /// </summary>
    public static bool TryParse(string line, string sourceFile, out LogRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            record = new LogRecord
            {
                Timestamp = ReadTimestamp(root),
                Severity = ReadSeverity(GetString(root, "level")),
                Component = GetString(root, "component"),
                Agent = GetString(root, "agent"),
                RunId = GetString(root, "runId"),
                Pipeline = GetString(root, "pipeline"),
                Action = GetString(root, "action"),
                Message = GetString(root, "message"),
                Exception = NullIfEmpty(GetString(root, "exception")),
                StackTrace = NullIfEmpty(GetString(root, "stackTrace")),
                SourceFile = sourceFile,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTime ReadTimestamp(JsonElement root)
    {
        var raw = GetString(root, "timestamp");
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dto))
            return dto.LocalDateTime;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        return DateTime.MinValue;
    }

    private static Severity ReadSeverity(string level) => level.ToLowerInvariant() switch
    {
        "error" => Severity.Error,
        "warning" or "warn" => Severity.Warning,
        "debug" => Severity.Debug,
        _ => Severity.Info,
    };

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
