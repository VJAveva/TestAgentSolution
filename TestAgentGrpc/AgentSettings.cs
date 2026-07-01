namespace TestAgentGrpc;

/// <summary>
/// Strongly-typed settings from appsettings.json → "AgentSettings".
/// </summary>
public sealed class AgentSettings
{
    private string _agentName = "";

    /// <summary>
    /// Friendly name used to identify this agent in the Controller's
    /// WatchList XML (e.g. "AppSerCI1").  Defaults to the machine name.
    /// NOTE: .NET config binding sets this to "" when appsettings has an empty string,
    /// so the getter always falls back to Environment.MachineName if blank.
    /// </summary>
    public string AgentName
    {
        get => string.IsNullOrWhiteSpace(_agentName) ? Environment.MachineName : _agentName;
        set => _agentName = value ?? "";
    }

    public int GrpcPort { get; set; } = 5200;
    public bool AllowPortFallback { get; set; }
    public int[] FallbackPorts { get; set; } = [];
    public string ControllerAddress { get; set; } = "http://localhost:5100";
    public string? AgentEndpoint { get; set; }

    /// <summary>
    /// Whether the advertised endpoint should use the <c>https://</c> scheme.
    /// This is NOT bound from configuration directly — it is set at startup from
    /// <c>AgentKestrel:EnableTls</c> so the endpoint the agent advertises to the
    /// controller always matches the actual Kestrel listener transport.
    /// Advertising the wrong scheme (e.g. http:// for a TLS-only listener) causes
    /// gRPC "SSL routines::wrong version number" handshake failures.
    /// Default: false (plaintext HTTP/2, matching AgentKestrel:EnableTls default).
    /// </summary>
    public bool AdvertiseTls { get; set; }
    // Registration
    public int RegistrationRetryCount { get; set; } = 3;
    public int RegistrationRetryIntervalSeconds { get; set; } = 30;

    // Heartbeat
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    // Execution history
    public int MaxExecutionHistoryCount { get; set; } = 200;
    public int MaxOutputLinesPerExecution { get; set; } = 5000;

    // Metrics
    public bool CollectSystemMetrics { get; set; } = true;

    /// <summary>
    /// When true, exposes a Prometheus <c>/metrics</c> scraping endpoint on a
    /// dedicated HTTP/1.1 listener (the gRPC port is HTTP/2-only). Default: true.
    /// </summary>
    public bool MetricsEndpointEnabled { get; set; } = true;

    /// <summary>
    /// Port for the Prometheus <c>/metrics</c> HTTP/1.1 listener. Default: 5210.
    /// </summary>
    public int MetricsPort { get; set; } = 5210;

    /// <summary>
    /// Hard safety-net timeout (in minutes) for executions with no explicit timeout (Timeout=0).
    /// Prevents the agent from being permanently stuck in "busy" state if a process hangs.
    /// Default: 120 minutes (2 hours).
    /// </summary>
    public int MaxExecutionTimeoutMinutes { get; set; } = 120;

    /// <summary>
    /// Grace period (in minutes) the watchdog waits beyond <see cref="MaxExecutionTimeoutMinutes"/>
    /// before forcibly resetting the agent. The normal CTS timeout should fire first;
    /// this is the nuclear fallback. Default: 5 minutes.
    /// </summary>
    public int WatchdogGraceMinutes { get; set; } = 5;

    public string GetResolvedEndpoint() =>
        AgentEndpoint ?? $"{(AdvertiseTls ? "https" : "http")}://{Environment.MachineName}:{GrpcPort}";
}
