namespace TestControllerGrpc.Services;

/// <summary>
/// Typed options for all configurable timeout and interval values used by
/// <see cref="IAgentGrpcDispatcher"/>, monitor polling, and channel management.
/// Binds to appsettings section "Controller:Timeouts".
/// </summary>
public sealed class ControllerTimeoutOptions
{
    public const string SectionName = "Controller:Timeouts";

    // ── Dispatcher ──

    /// <summary>Timeout for TestConnectionAsync gRPC calls (seconds). Default: 5.</summary>
    public int TestConnectionTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout for PingAsync gRPC call (seconds). Default: 3.</summary>
    public int PingTimeoutSeconds { get; set; } = 3;

    /// <summary>Timeout for Diagnostics TCP port check (seconds). Default: 3.</summary>
    public int DiagnosticsTcpTimeoutSeconds { get; set; } = 3;

    /// <summary>Timeout for Diagnostics HTTP endpoint check (seconds). Default: 3.</summary>
    public int DiagnosticsHttpTimeoutSeconds { get; set; } = 3;

    /// <summary>Timeout for Diagnostics gRPC calls (seconds). Default: 5.</summary>
    public int DiagnosticsGrpcTimeoutSeconds { get; set; } = 5;

    /// <summary>Maximum total wait time for WaitForAgentFree (seconds). Default: 30.</summary>
    public int WaitForAgentFreeMaxSeconds { get; set; } = 30;

    /// <summary>Maximum wait time for busy-recovery retry loop (seconds). Default: 120.</summary>
    public int BusyRecoveryMaxSeconds { get; set; } = 120;

    /// <summary>Interval between busy-recovery checks (seconds). Default: 10.</summary>
    public int BusyRecoveryIntervalSeconds { get; set; } = 10;

    // ── Channel ──

    /// <summary>TCP connect timeout for new channels (seconds). Default: 30.</summary>
    public int ChannelConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>HTTP/2 keep-alive ping interval (seconds). Default: 60.</summary>
    public int KeepAlivePingDelaySeconds { get; set; } = 60;

    /// <summary>HTTP/2 keep-alive ping response timeout (seconds). Default: 30.</summary>
    public int KeepAlivePingTimeoutSeconds { get; set; } = 30;

    /// <summary>Idle connection pool timeout (minutes). Default: 5.</summary>
    public int PooledConnectionIdleMinutes { get; set; } = 5;

    /// <summary>Circuit breaker break duration (seconds). Default: 15.</summary>
    public int CircuitBreakerBreakSeconds { get; set; } = 15;

    /// <summary>Consecutive failures before auto-resetting channel. Default: 10.</summary>
    public int AutoResetFailureThreshold { get; set; } = 10;

    // ── Monitor polling ──

    /// <summary>Telemetry polling interval (milliseconds). Default: 2000.</summary>
    public int TelemetryPollIntervalMs { get; set; } = 2000;

    /// <summary>Session elapsed timer interval (milliseconds). Default: 1000.</summary>
    public int SessionElapsedTimerMs { get; set; } = 1000;

    // ── Resilience ──

    /// <summary>Polly outer safety-net timeout (hours). Default: 24.</summary>
    public int OuterSafetyNetTimeoutHours { get; set; } = 24;

    /// <summary>Polly retry max attempts for Unavailable/DeadlineExceeded. Default: 3.</summary>
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>Polly retry base delay (seconds). Default: 1.</summary>
    public int RetryDelaySeconds { get; set; } = 1;

    /// <summary>Wait time (seconds) for agent recovery on UNAVAILABLE before failing. Default: 60. Set to 0 to disable.</summary>
    public int UnavailableRecoverySeconds { get; set; } = 60;

    /// <summary>Poll interval (seconds) during unavailable recovery wait. Default: 10.</summary>
    public int UnavailableRecoveryPollIntervalSeconds { get; set; } = 10;

    // ── Execution ──

    /// <summary>Default action execution timeout when none specified in XML (seconds). Default: 7200 (2 hours).</summary>
    public int DefaultActionTimeoutSeconds { get; set; } = 7200;

    /// <summary>Maximum allowed action timeout regardless of XML config (seconds). Default: 172800 (48 hours).</summary>
    public int MaxActionTimeoutSeconds { get; set; } = 172_800;

    /// <summary>SignalR broadcast send timeout (seconds). Default: 5.</summary>
    public int BroadcastTimeoutSeconds { get; set; } = 5;

    // ── Lock Recovery ──

    /// <summary>Lock recovery orphan check base interval (seconds). Default: 300 (5 min).</summary>
    public int LockRecoveryBaseIntervalSeconds { get; set; } = 300;

    /// <summary>Lock recovery maximum backoff interval (seconds). Default: 1800 (30 min).</summary>
    public int LockRecoveryMaxIntervalSeconds { get; set; } = 1800;
}

/// <summary>
/// Typed options for polling intervals used by file watchers,
/// telemetry monitors, and other periodic services.
/// Binds to appsettings section "Controller:Polling".
/// </summary>
public sealed class PollingOptions
{
    public const string SectionName = "Controller:Polling";

    /// <summary>File watcher debounce interval (milliseconds). Default: 500.</summary>
    public int FileWatcherDebounceMs { get; set; } = 500;

    /// <summary>Agent heartbeat publish interval (milliseconds). Default: 1000.</summary>
    public int HeartbeatPublishIntervalMs { get; set; } = 1000;

    /// <summary>WatchList hot-reload debounce (milliseconds). Default: 2000.</summary>
    public int WatchListReloadDebounceMs { get; set; } = 2000;

    /// <summary>Build results cache refresh interval (seconds). Default: 60.</summary>
    public int BuildResultsCacheRefreshSeconds { get; set; } = 60;

    /// <summary>Dashboard session elapsed timer interval (milliseconds). Default: 1000.</summary>
    public int SessionElapsedTimerMs { get; set; } = 1000;
}
