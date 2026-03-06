namespace TestAgentGrpc.Services;

/// <summary>
/// Tracks connection health to the controller, raising events on
/// state transitions (connected ? disconnected and vice versa).
/// Also maintains an in-memory ring buffer of recent communication events
/// for display in the Connection Details form.
/// </summary>
public sealed class ConnectionHealthMonitor
{
    private readonly object _lock = new();
    private readonly int _consecutiveFailureThreshold = 3;

    private const int MaxLogEntries = 100;
    private readonly List<CommunicationLogEntry> _log = new();

    // ?? Public state ???????????????????????????????????????????????????

    public string? ControllerName { get; private set; }
    public string? ControllerAddress { get; private set; }
    public bool IsConnected { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public DateTime? LastSuccessfulHeartbeat { get; private set; }
    public DateTime? LastFailedHeartbeat { get; private set; }
    public long TotalHeartbeatsSent { get; private set; }
    public long TotalHeartbeatsFailed { get; private set; }
    public DateTime? RegistrationTimestamp { get; private set; }
    public long CurrentSuccessStreak { get; private set; }

    /// <summary>Time since the last recovery (or since initial registration if no failures).</summary>
    public TimeSpan UptimeSinceLastRecovery =>
        _lastRecoveryUtc is not null ? DateTime.UtcNow - _lastRecoveryUtc.Value : TimeSpan.Zero;

    /// <summary>Duration of the current or last downtime period, if any.</summary>
    public TimeSpan? DowntimeDuration =>
        _downtimeStartUtc is not null
            ? (IsConnected ? _lastRecoveryUtc - _downtimeStartUtc : DateTime.UtcNow - _downtimeStartUtc)
            : null;

    private DateTime? _lastRecoveryUtc;
    private DateTime? _downtimeStartUtc;
    private string? _lastError;

    public string? LastError => _lastError;

    // ?? Events ?????????????????????????????????????????????????????????

    /// <summary>Fired when the connection is considered lost (consecutive failures ? threshold).</summary>
    public event Action? ConnectionLost;

    /// <summary>Fired when the connection recovers after being lost.</summary>
    public event Action? ConnectionRecovered;

    /// <summary>Fired on every status transition (connected/warning/disconnected/unregistered).</summary>
    public event Action<ConnectionStatus>? ConnectionStatusChanged;

    // ?? Recording methods ??????????????????????????????????????????????

    public void RecordHeartbeatSuccess()
    {
        bool wasDisconnected;
        lock (_lock)
        {
            wasDisconnected = !IsConnected && RegistrationTimestamp is not null;
            ConsecutiveFailures = 0;
            CurrentSuccessStreak++;
            LastSuccessfulHeartbeat = DateTime.UtcNow;
            TotalHeartbeatsSent++;

            if (!IsConnected && RegistrationTimestamp is not null)
            {
                IsConnected = true;
                _lastRecoveryUtc = DateTime.UtcNow;
            }

            AddLogEntry("HeartbeatOK", "Heartbeat acknowledged", CommunicationSeverity.Success);
        }

        if (wasDisconnected)
            ConnectionRecovered?.Invoke();

        ConnectionStatusChanged?.Invoke(GetStatus());
    }

    public void RecordHeartbeatFailure(string error)
    {
        bool justLost;
        lock (_lock)
        {
            ConsecutiveFailures++;
            CurrentSuccessStreak = 0;
            LastFailedHeartbeat = DateTime.UtcNow;
            TotalHeartbeatsFailed++;
            _lastError = error;

            justLost = ConsecutiveFailures == _consecutiveFailureThreshold && IsConnected;
            if (justLost)
            {
                IsConnected = false;
                _downtimeStartUtc = DateTime.UtcNow;
            }

            var severity = justLost ? CommunicationSeverity.Error : CommunicationSeverity.Warning;
            AddLogEntry("HeartbeatFail", error, severity);
        }

        if (justLost)
            ConnectionLost?.Invoke();

        ConnectionStatusChanged?.Invoke(GetStatus());
    }

    public void RecordRegistration(string controllerName, string address)
    {
        lock (_lock)
        {
            ControllerName = controllerName;
            ControllerAddress = address;
            RegistrationTimestamp = DateTime.UtcNow;
            IsConnected = true;
            ConsecutiveFailures = 0;
            CurrentSuccessStreak = 0;
            _lastRecoveryUtc = DateTime.UtcNow;
            _downtimeStartUtc = null;

            AddLogEntry("Registered", $"Registered with {controllerName} at {address}", CommunicationSeverity.Success);
        }

        ConnectionStatusChanged?.Invoke(ConnectionStatus.Connected);
    }

    public void RecordUnregistration()
    {
        lock (_lock)
        {
            IsConnected = false;
            RegistrationTimestamp = null;

            AddLogEntry("Unregistered", "Agent unregistered", CommunicationSeverity.Info);
        }

        ConnectionStatusChanged?.Invoke(ConnectionStatus.NeverRegistered);
    }

    public ConnectionStatus GetStatus()
    {
        if (RegistrationTimestamp is null)
            return ConnectionStatus.NeverRegistered;
        if (ConsecutiveFailures == 0)
            return ConnectionStatus.Connected;
        if (ConsecutiveFailures < _consecutiveFailureThreshold)
            return ConnectionStatus.Warning;
        return ConnectionStatus.Disconnected;
    }

    // ?? Communication log ??????????????????????????????????????????????

    /// <summary>Returns a snapshot of recent communication events (most recent first).</summary>
    public List<CommunicationLogEntry> GetRecentLog()
    {
        lock (_lock)
        {
            var copy = new List<CommunicationLogEntry>(_log);
            copy.Reverse();
            return copy;
        }
    }

    private void AddLogEntry(string eventName, string detail, CommunicationSeverity severity)
    {
        _log.Add(new CommunicationLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Event = eventName,
            Detail = detail,
            Severity = severity,
        });

        while (_log.Count > MaxLogEntries)
            _log.RemoveAt(0);
    }
}

public enum ConnectionStatus
{
    NeverRegistered,
    Connected,
    Warning,
    Disconnected,
}

public sealed class CommunicationLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Event { get; init; } = "";
    public string Detail { get; init; } = "";
    public CommunicationSeverity Severity { get; init; }
}

public enum CommunicationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}
