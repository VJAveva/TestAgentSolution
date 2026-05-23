using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TestAgentGrpc.Services;

/// <summary>
/// Internal record representing a single audit log entry.
/// Serialized as JSON Lines to daily .jsonl files.
/// </summary>
public sealed record AuditEntry
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Event { get; init; } = "";
    public string Severity { get; init; } = "Info";
    public string? ExecutionId { get; init; }
    public string? Source { get; init; }
    public string? Controller { get; init; }
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public string? Credentials { get; init; }
    public int? Pid { get; init; }
    public int? ExitCode { get; init; }
    public long? DurationMs { get; init; }
    public string? Detail { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Persistent audit logging service.
///
/// Writes JSON Lines (.jsonl) to a configurable directory with:
///   � One file per day (audit_yyyy-MM-dd.jsonl)
///   � Thread-safe non-blocking writes via <see cref="Channel{T}"/>
///   � Automatic directory creation and old-file retention cleanup
///   � Max file size rollover with numbered suffixes
/// </summary>
public sealed class AuditLogger : IHostedService, IDisposable
{
    private readonly AuditSettings _settings;
    private readonly ILogger<AuditLogger> _logger;
    private readonly Channel<AuditEntry> _channel;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public AuditLogger(IOptions<AuditSettings> settings, ILogger<AuditLogger> logger)
    {
        _settings = settings.Value;
        _logger   = logger;
        _channel  = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    // ?? IHostedService ?????????????????????????????????????????????????

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Audit logging is disabled");
            return Task.CompletedTask;
        }

        try
        {
            Directory.CreateDirectory(_settings.LogDirectory);
            PurgeOldFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize audit log directory: {Dir}", _settings.LogDirectory);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));

        _logger.LogInformation("Audit logger started ? {Dir}", _settings.LogDirectory);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        if (_flushTask is not null)
        {
            try { await _flushTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { _cts?.Cancel(); }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    // ?? Public API ?????????????????????????????????????????????????????

    /// <summary>
    /// Enqueues an audit entry for asynchronous writing. Non-blocking and thread-safe.
    /// </summary>
    public void Log(string eventName, string severity = "Info",
        string? executionId = null, string? source = null,
        string? controller = null, string? command = null,
        string? arguments = null, string? credentials = null,
        int? pid = null, int? exitCode = null,
        long? durationMs = null, string? detail = null,
        string? correlationId = null)
    {
        if (!_settings.Enabled) return;

        var entry = new AuditEntry
        {
            Timestamp   = DateTime.UtcNow,
            Event       = eventName,
            Severity    = severity,
            ExecutionId = executionId,
            Source      = SecurityRedactor.Redact(source),
            Controller  = SecurityRedactor.Redact(controller),
            Command     = SecurityRedactor.Redact(command),
            Arguments   = SecurityRedactor.Redact(arguments),
            Credentials = string.IsNullOrWhiteSpace(credentials) ? credentials : SecurityRedactor.Redacted,
            Pid         = pid,
            ExitCode    = exitCode,
            DurationMs  = durationMs,
            Detail      = SecurityRedactor.Redact(detail),
            CorrelationId = correlationId,
        };

        _channel.Writer.TryWrite(entry);
    }

    /// <summary>
    /// Reads audit entries from log files matching the given criteria.
    /// Used by the gRPC <c>GetAuditLog</c> RPC.
    /// </summary>
    public List<AuditEntry> ReadEntries(string? fromDate, string? toDate,
        string? eventFilter, int maxEntries = 500)
    {
        var results = new List<AuditEntry>();
        if (!_settings.Enabled || !Directory.Exists(_settings.LogDirectory))
            return results;

        var from = ParseDate(fromDate) ?? DateTime.UtcNow.Date.AddDays(-7);
        var to   = ParseDate(toDate)   ?? DateTime.UtcNow.Date;

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var pattern = $"audit_{date:yyyy-MM-dd}*.jsonl";
            var files = Directory.GetFiles(_settings.LogDirectory, pattern);
            Array.Sort(files);

            foreach (var file in files)
            {
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var entry = JsonSerializer.Deserialize<AuditEntry>(line, s_jsonOptions);
                        if (entry is null) continue;

                        if (!string.IsNullOrEmpty(eventFilter) && !MatchesFilter(entry.Event, eventFilter))
                            continue;

                        results.Add(entry);
                        if (results.Count >= maxEntries)
                            return results;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read audit file: {File}", file);
                }
            }
        }

        return results;
    }

    // ?? Background flush loop ??????????????????????????????????????????

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        StreamWriter? writer = null;
        string? currentFilePath = null;

        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var targetPath = GetTargetFilePath();

                    // Rotate writer if date changed or file exceeded size limit
                    if (writer is null || currentFilePath != targetPath)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync();
                        }
                        currentFilePath = targetPath;
                        writer = new StreamWriter(currentFilePath, append: true) { AutoFlush = true };
                    }

                    var json = JsonSerializer.Serialize(entry, s_jsonOptions);
                    await writer.WriteLineAsync(json);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write audit entry");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync();
        }
    }

    // ?? File management ????????????????????????????????????????????????

    private string GetTargetFilePath()
    {
        var baseName = $"audit_{DateTime.UtcNow:yyyy-MM-dd}";
        var basePath = Path.Combine(_settings.LogDirectory, $"{baseName}.jsonl");
        var maxBytes = (long)_settings.MaxFileSizeMb * 1024 * 1024;

        if (!File.Exists(basePath))
            return basePath;

        var info = new FileInfo(basePath);
        if (info.Length < maxBytes)
            return basePath;

        // Rollover: find next available numbered file
        for (int i = 1; i < 1000; i++)
        {
            var rolloverPath = Path.Combine(_settings.LogDirectory, $"{baseName}_{i:D3}.jsonl");
            if (!File.Exists(rolloverPath))
                return rolloverPath;

            var rolloverInfo = new FileInfo(rolloverPath);
            if (rolloverInfo.Length < maxBytes)
                return rolloverPath;
        }

        // Exhausted rollover slots � overwrite last
        return Path.Combine(_settings.LogDirectory, $"{baseName}_999.jsonl");
    }

    private void PurgeOldFiles()
    {
        if (_settings.RetentionDays <= 0) return;

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_settings.RetentionDays);
            foreach (var file in Directory.GetFiles(_settings.LogDirectory, "audit_*.jsonl"))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc < cutoff)
                {
                    fi.Delete();
                    _logger.LogDebug("Purged old audit file: {File}", fi.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge old audit files");
        }
    }

    // ?? Helpers ????????????????????????????????????????????????????????

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static bool MatchesFilter(string eventName, string filter)
    {
        if (filter.EndsWith('*'))
            return eventName.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase);
        return eventName.Equals(filter, StringComparison.OrdinalIgnoreCase);
    }
}
