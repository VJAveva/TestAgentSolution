using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Central <see cref="ILogSink"/> that ships log entries to a Seq server using the
/// Compact Log Event Format (CLEF). Entries are buffered in-memory and flushed on a
/// background timer in batches, so the logging hot path never blocks on the network.
///
/// Failure-safe by design: <see cref="Emit"/> never throws, the buffer is bounded
/// (oldest dropped under back-pressure), and flush failures are swallowed (the local
/// AppLogger file sinks remain the source of truth if Seq is unreachable).
///
/// The same NDJSON/CLEF payload is consumable by ELK via a compatible ingestion
/// endpoint; only the target URL/headers differ, so the abstraction is not Seq-locked.
/// </summary>
public sealed class SeqLogSink : ILogSink
{
    private readonly ConcurrentQueue<AppLogEntry> _queue = new();
    private readonly HttpClient _http;
    private readonly LogSinkOptions _options;
    private readonly LogLevel? _minLevel;
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Uri _ingestUri;
    private int _queueCount;
    private volatile bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public SeqLogSink(LogSinkOptions options, LogLevel? minLevel = null, HttpClient? httpClient = null)
    {
        _options = options;
        _minLevel = minLevel;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var baseUrl = options.SeqUrl.TrimEnd('/');
        _ingestUri = new Uri($"{baseUrl}/api/events/raw?clef");
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Seq-ApiKey", options.ApiKey);

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.FlushIntervalSeconds));
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, interval, interval);
    }

    public void Emit(AppLogEntry entry)
    {
        if (_disposed) return;
        if (_minLevel is { } min && entry.Level < min) return;

        _queue.Enqueue(entry);

        // Bounded buffer: drop oldest beyond capacity to protect memory if Seq is down.
        if (Interlocked.Increment(ref _queueCount) > _options.MaxBufferedEvents
            && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queueCount);
        }
    }

    private async Task FlushAsync()
    {
        if (_disposed || _queue.IsEmpty) return;
        if (!await _flushGate.WaitAsync(0)) return; // a flush is already running

        try
        {
            var batch = new List<AppLogEntry>(_options.BatchSize);
            while (batch.Count < _options.BatchSize && _queue.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _queueCount);
                batch.Add(entry);
            }

            if (batch.Count == 0) return;

            var payload = BuildClef(batch);
            using var content = new StringContent(payload, Encoding.UTF8, "application/vnd.serilog.clef");

            try
            {
                using var response = await _http.PostAsync(_ingestUri, content);
                if (!response.IsSuccessStatusCode)
                    Requeue(batch);
            }
            catch
            {
                // Network/transient failure — requeue (bounded) so a blip doesn't lose logs.
                Requeue(batch);
            }
        }
        catch
        {
            // Never let the flusher throw.
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void Requeue(List<AppLogEntry> batch)
    {
        foreach (var entry in batch)
        {
            _queue.Enqueue(entry);
            if (Interlocked.Increment(ref _queueCount) > _options.MaxBufferedEvents
                && _queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queueCount);
            }
        }
    }

    private static string BuildClef(IReadOnlyList<AppLogEntry> batch)
    {
        var sb = new StringBuilder(batch.Count * 256);
        foreach (var entry in batch)
        {
            sb.AppendLine(BuildClefLine(entry));
        }
        return sb.ToString();
    }

    private static string BuildClefLine(AppLogEntry entry)
    {
        // CLEF: @t timestamp, @l level (omitted for Information), @m message, @x exception.
        // Remaining keys are arbitrary structured properties Seq/ELK can query on.
        var obj = new Dictionary<string, object?>
        {
            ["@t"] = entry.Timestamp.ToString("o"),
            ["@m"] = entry.Message,
            ["@x"] = entry.Exception ?? entry.StackTrace,
            ["Component"] = entry.Category,
            ["Agent"] = entry.Agent,
            ["RunId"] = entry.RunId,
            ["CorrelationId"] = entry.CorrelationId,
            ["Pipeline"] = entry.Pipeline,
            ["Action"] = entry.Action,
            ["Thread"] = entry.ThreadId,
            ["Seq"] = entry.Sequence,
            ["ElapsedMs"] = entry.ElapsedMs > 0 ? entry.ElapsedMs : null,
        };

        var level = MapLevel(entry.Level);
        if (level is not null)
            obj["@l"] = level;

        try
        {
            return JsonSerializer.Serialize(obj, JsonOptions);
        }
        catch
        {
            // Last-resort minimal event so a serialization failure never breaks the batch.
            return $"{{\"@t\":\"{entry.Timestamp:o}\",\"@m\":\"(serialization failed)\"}}";
        }
    }

    private static string? MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Verbose",
        LogLevel.Debug => "Debug",
        LogLevel.Information => null, // CLEF default; omit to keep payload small
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => null,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();
        try { FlushAsync().GetAwaiter().GetResult(); } catch { /* best-effort final flush */ }

        _flushGate.Dispose();
        _http.Dispose();
    }
}
