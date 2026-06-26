using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Abstraction for an external log sink that receives every <see cref="AppLogEntry"/>
/// the <see cref="AppLogger"/> emits, so logs can be shipped off-box to a central
/// query surface (e.g. Seq / ELK) instead of being trapped in per-VM files —
/// without replacing AppLogger's existing local file + in-memory ring sinks.
///
/// Implementations MUST be non-blocking and fail-safe: <see cref="Emit"/> is called
/// on the logging hot path and must never throw or block. Buffer in-memory and flush
/// asynchronously.
/// </summary>
public interface ILogSink : IDisposable
{
    /// <summary>
    /// Queues an entry for off-box delivery. Must return immediately and never throw.
    /// </summary>
    void Emit(AppLogEntry entry);
}

/// <summary>
/// Configuration for the optional central log sink. Bound from the
/// "Logging:CentralSink" configuration section. Disabled unless
/// <see cref="Enabled"/> is true and a target URL is provided.
/// </summary>
public sealed class LogSinkOptions
{
    public const string SectionName = "Logging:CentralSink";

    /// <summary>Master on/off switch (default: false — local files only).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL of the Seq server (e.g. "http://seq.host:5341"). Required when enabled.
    /// </summary>
    public string SeqUrl { get; set; } = "";

    /// <summary>Optional Seq API key (sent as the X-Seq-ApiKey header).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum events shipped per HTTP flush (default: 100).</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Seconds between background flushes (default: 2).</summary>
    public int FlushIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Maximum events held in memory before the oldest are dropped to protect
    /// memory when the sink is unreachable (default: 10000).
    /// </summary>
    public int MaxBufferedEvents { get; set; } = 10000;

    /// <summary>
    /// Optional minimum level to forward (e.g. "Warning"). Entries below this are
    /// not shipped. Null/empty forwards everything (default).
    /// </summary>
    public string? MinimumLevel { get; set; }
}

/// <summary>
/// Factory that builds the configured central <see cref="ILogSink"/>, or returns
/// null when central logging is disabled / unconfigured. Keeps host wiring to a
/// single call so AppLogger construction stays unchanged when the sink is off.
/// </summary>
public static class CentralLogSink
{
    /// <summary>
    /// Creates the central sink from bound <paramref name="options"/>, or returns
    /// null when disabled or missing a target URL.
    /// </summary>
    public static ILogSink? Create(LogSinkOptions options)
    {
        if (options is null || !options.Enabled || string.IsNullOrWhiteSpace(options.SeqUrl))
            return null;

        LogLevel? minLevel = null;
        if (!string.IsNullOrWhiteSpace(options.MinimumLevel)
            && Enum.TryParse<LogLevel>(options.MinimumLevel, ignoreCase: true, out var parsed))
        {
            minLevel = parsed;
        }

        return new SeqLogSink(options, minLevel);
    }
}
