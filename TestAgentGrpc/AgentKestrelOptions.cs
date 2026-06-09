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

    // ── TLS / mTLS settings ─────────────────────────────────────────────

    /// <summary>
    /// Whether TLS is enabled on the agent listener.
    /// When true, a secondary HTTPS/2 endpoint is configured (or only HTTPS if TlsOnly).
    /// </summary>
    public bool EnableTls { get; set; }

    /// <summary>
    /// Port for the TLS listener. Only used when <see cref="EnableTls"/> is true.
    /// </summary>
    public int TlsPort { get; set; } = 5443;

    /// <summary>
    /// Certificate thumbprint (from LocalMachine\My store) for the TLS listener.
    /// Either this or <see cref="CertFilePath"/> must be set when TLS is enabled.
    /// </summary>
    public string CertThumbprint { get; set; } = "";

    /// <summary>
    /// Path to a PFX file for the TLS listener (alternative to cert store).
    /// </summary>
    public string CertFilePath { get; set; } = "";

    /// <summary>
    /// Password for the PFX file (if applicable).
    /// </summary>
    public string CertPassword { get; set; } = "";

    /// <summary>
    /// Whether mutual TLS is required (client must present a certificate).
    /// </summary>
    public bool RequireClientCertificate { get; set; }
}
