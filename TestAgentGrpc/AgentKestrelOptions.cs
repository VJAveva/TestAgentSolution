namespace TestAgentGrpc;

/// <summary>
/// Typed Kestrel configuration for the agent's HTTP/2 listener.
/// Bound from appsettings.json → "AgentKestrel".
/// </summary>
public sealed class AgentKestrelOptions
{
    /// <summary>
    /// How long Kestrel keeps an idle HTTP/2 connection alive.
    /// Default: 12 hours (long test executions can run 8+ hours; the in-stream
    /// heartbeat emits progress events every 30s keeping the stream active,
    /// but this must exceed the longest expected execution time as a safety margin).
    /// </summary>
    public int KeepAliveTimeoutMinutes { get; set; } = 720;

    /// <summary>
    /// Whether to disable minimum request body data rate enforcement.
    /// When true, Kestrel won't kill slow/idle connections.
    /// Default: true (required for long-running test installs).
    /// </summary>
    public bool DisableMinRequestBodyDataRate { get; set; } = true;

    /// <summary>
    /// Whether to disable minimum response data rate enforcement.
    /// Default: true.
    /// </summary>
    public bool DisableMinResponseDataRate { get; set; } = true;

    /// <summary>
    /// If true, logs a warning at startup when the agent is listening on plaintext HTTP/2
    /// (i.e., no TLS configured). Should be true in production environments.
    /// </summary>
    public bool WarnOnPlaintextHttp2 { get; set; } = true;
}
