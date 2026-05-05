using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Abstraction over the source of real-time execution updates for the
/// Execution Dashboard window. Two implementations are supported:
///
///   - <see cref="InProcessExecutionFeed"/> ? used when the dashboard runs
///     inside the same WPF process as the controller services. Wires
///     directly into ExecutionSessionManager / EventAggregator without
///     SignalR (zero latency, no serialization, no extra moving parts).
///
///   - <see cref="SignalRExecutionFeed"/> ? used when the dashboard runs in
///     a separate process (standalone monitoring tool, ops console,
///     different machine) and needs to observe a remote ControllerService
///     over the network.
///
/// The same <c>ExecutionDashboardVM</c> consumes either feed via the
/// events declared here. This keeps a single source of truth for the UI
/// layer and lets a future standalone Dashboard project be a thin shell
/// that picks the SignalR transport.
/// </summary>
public interface IExecutionFeed : IAsyncDisposable
{
    /// <summary>Raised when the underlying transport's connection state changes.</summary>
    event EventHandler<FeedConnectionState>? ConnectionChanged;

    /// <summary>Raised when an action's status/progress is updated.</summary>
    event EventHandler<ActionProgressMessage>? ActionProgress;

    /// <summary>Raised when an action group's status changes.</summary>
    event EventHandler<GroupProgressMessage>? GroupProgress;

    /// <summary>Raised when a session starts.</summary>
    event EventHandler<ExecutionStartedMessage>? ExecutionStarted;

    /// <summary>Raised when a session ends (success/failure/cancel).</summary>
    event EventHandler<ExecutionCompletedMessage>? ExecutionCompleted;

    /// <summary>Raised when a batch of log entries arrives.</summary>
    event EventHandler<IReadOnlyList<LogEntryMessage>>? LogsAppended;

    /// <summary>Current connection state (for footer display).</summary>
    FeedConnectionState State { get; }

    /// <summary>Establishes the underlying transport (network or in-process).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops the transport. Safe to call multiple times.</summary>
    Task StopAsync(CancellationToken ct = default);
}

public enum FeedConnectionState { Disconnected, Connecting, Connected, Reconnecting }

// ?? Wire-format messages (intentionally simple records so SignalR JSON ??
// serialization works without custom converters and the same shape can
// flow through the in-process feed). ??????????????????????????????????????

public sealed record ActionProgressMessage(
    string SessionId, string AgentName, string ActionTag,
    string Command, string Status, int ProgressPercent,
    DateTime StartedUtc, double DurationSeconds);

public sealed record GroupProgressMessage(
    string SessionId, string GroupTag, string Status);

public sealed record ExecutionStartedMessage(
    string SessionId, string WatchItemTag, string EventType,
    string UserId, string Source, DateTime StartedUtc);

public sealed record ExecutionCompletedMessage(
    string SessionId, string Status, DateTime CompletedUtc);

public sealed record LogEntryMessage(
    DateTime Timestamp, string SessionId, string SessionName,
    string AgentName, string Severity, string Message);

/// <summary>
/// Remote feed that connects to the existing /hubs/controller SignalR hub
/// (provided by the shared TestController.Api). Surfaces the same events
/// as the in-process feed so the dashboard ViewModel doesn't care which
/// transport is in play.
///
/// Resilience notes:
///   - withAutomaticReconnect with exponential backoff (5s, 10s, 30s, 60s).
///   - Server timeout: 60s (matches the WebApi appsettings).
///   - All hub callbacks marshal to a configured Dispatcher before raising
///     CLR events ? the consumer (UI layer) is guaranteed UI-thread.
/// </summary>
public sealed class SignalRExecutionFeed : IExecutionFeed
{
    private readonly Uri _hubUri;
    private readonly string _userId;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly ILogger<SignalRExecutionFeed>? _logger;
    private HubConnection? _conn;
    private FeedConnectionState _state = FeedConnectionState.Disconnected;

    public event EventHandler<FeedConnectionState>? ConnectionChanged;
    public event EventHandler<ActionProgressMessage>? ActionProgress;
    public event EventHandler<GroupProgressMessage>? GroupProgress;
    public event EventHandler<ExecutionStartedMessage>? ExecutionStarted;
    public event EventHandler<ExecutionCompletedMessage>? ExecutionCompleted;
    public event EventHandler<IReadOnlyList<LogEntryMessage>>? LogsAppended;

    public FeedConnectionState State => _state;

    public SignalRExecutionFeed(
        Uri hubUri,
        string userId,
        System.Windows.Threading.Dispatcher uiDispatcher,
        ILogger<SignalRExecutionFeed>? logger = null)
    {
        _hubUri = hubUri ?? throw new ArgumentNullException(nameof(hubUri));
        _userId = userId ?? "";
        _dispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_conn != null) return;

        SetState(FeedConnectionState.Connecting);

        _conn = new HubConnectionBuilder()
            .WithUrl(_hubUri)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
            })
            .Build();

        _conn.ServerTimeout = TimeSpan.FromSeconds(60);
        _conn.KeepAliveInterval = TimeSpan.FromSeconds(15);

        // Wire hub broadcasts (names match TestController.Api SignalRBridge)
        _conn.On<ActionProgressDto>("ActionProgress", dto =>
            Raise(() => ActionProgress?.Invoke(this, new ActionProgressMessage(
                dto.SessionId ?? "", dto.AgentName ?? "",
                dto.ActionTag ?? dto.Command ?? "", dto.Command ?? "",
                dto.Status ?? "Pending", dto.ProgressPercent,
                dto.StartedUtc ?? DateTime.UtcNow, dto.DurationSeconds))));

        _conn.On<GroupProgressDto>("GroupProgress", dto =>
            Raise(() => GroupProgress?.Invoke(this, new GroupProgressMessage(
                dto.SessionId ?? "", dto.GroupTag ?? "", dto.Status ?? "Pending"))));

        _conn.On<ExecutionStartedDto>("ExecutionStarted", dto =>
            Raise(() => ExecutionStarted?.Invoke(this, new ExecutionStartedMessage(
                dto.SessionId ?? "", dto.WatchItemTag ?? "", dto.EventType ?? "",
                dto.UserId ?? "", dto.Source ?? "", dto.StartedUtc ?? DateTime.UtcNow))));

        _conn.On<ExecutionCompletedDto>("ExecutionCompleted", dto =>
            Raise(() => ExecutionCompleted?.Invoke(this, new ExecutionCompletedMessage(
                dto.SessionId ?? "", dto.Status ?? "Completed",
                dto.CompletedUtc ?? DateTime.UtcNow))));

        // Subscribe to individual log entries (server sends "LogEntry" singular)
        _conn.On<LogEntryDto>("LogEntry", dto =>
            Raise(() =>
            {
                var entry = new LogEntryMessage(
                    dto.Timestamp ?? DateTime.UtcNow,
                    dto.SessionId ?? "", dto.SessionName ?? "",
                    dto.AgentName ?? "", dto.Severity ?? "Info",
                    dto.Message ?? "");
                LogsAppended?.Invoke(this, [entry]);
            }));

        // Also subscribe to AgentOutput so stdout/stderr flows into the log
        _conn.On<AgentOutputDto>("AgentOutput", dto =>
            Raise(() =>
            {
                var entry = new LogEntryMessage(
                    DateTime.UtcNow,
                    dto.SessionId ?? "", "",
                    dto.AgentName ?? "", dto.Kind == "stderr" ? "Error" : "Info",
                    dto.Line ?? "");
                LogsAppended?.Invoke(this, [entry]);
            }));

        _conn.Reconnecting += err =>
        {
            _logger?.LogWarning(err, "[ExecutionFeed] Reconnecting to {Url}", _hubUri);
            SetState(FeedConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _conn.Reconnected += async _ =>
        {
            _logger?.LogInformation("[ExecutionFeed] Reconnected to {Url}", _hubUri);
            await RejoinGroupsAsync().ConfigureAwait(false);
            SetState(FeedConnectionState.Connected);
        };
        _conn.Closed += err =>
        {
            _logger?.LogWarning(err, "[ExecutionFeed] Connection closed");
            SetState(FeedConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        try
        {
            await _conn.StartAsync(ct).ConfigureAwait(false);
            await RejoinGroupsAsync().ConfigureAwait(false);
            SetState(FeedConnectionState.Connected);
            _logger?.LogInformation("[ExecutionFeed] Connected to {Url}", _hubUri);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ExecutionFeed] Failed to connect to {Url}", _hubUri);
            SetState(FeedConnectionState.Disconnected);
            throw;
        }
    }

    private async Task RejoinGroupsAsync()
    {
        if (_conn == null) return;
        try { await _conn.InvokeAsync("JoinAllSessions").ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "JoinAllSessions failed"); }
        if (!string.IsNullOrEmpty(_userId))
        {
            try { await _conn.InvokeAsync("JoinAsUser", _userId).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "JoinAsUser failed"); }
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_conn == null) return;
        try { await _conn.StopAsync(ct).ConfigureAwait(false); }
        catch { /* ignore ? shutdown */ }
        SetState(FeedConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn == null) return;
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
        _conn = null;
    }

    private void Raise(Action body)
    {
        if (_dispatcher.CheckAccess()) body();
        else _dispatcher.BeginInvoke(body);
    }

    private void SetState(FeedConnectionState newState)
    {
        if (_state == newState) return;
        _state = newState;
        Raise(() => ConnectionChanged?.Invoke(this, newState));
    }

    // ?? Wire DTOs (loose, defensive: hub may add fields without breaking) ??

    private sealed class ActionProgressDto
    {
        public string? SessionId { get; set; }
        public string? AgentName { get; set; }
        public string? ActionTag { get; set; }
        public string? Command { get; set; }
        public string? Status { get; set; }
        public int ProgressPercent { get; set; }
        public DateTime? StartedUtc { get; set; }
        public double DurationSeconds { get; set; }
    }

    private sealed class GroupProgressDto
    {
        public string? SessionId { get; set; }
        public string? GroupTag { get; set; }
        public string? Status { get; set; }
    }

    private sealed class ExecutionStartedDto
    {
        public string? SessionId { get; set; }
        public string? WatchItemTag { get; set; }
        public string? EventType { get; set; }
        public string? UserId { get; set; }
        public string? Source { get; set; }
        public DateTime? StartedUtc { get; set; }
    }

    private sealed class ExecutionCompletedDto
    {
        public string? SessionId { get; set; }
        public string? Status { get; set; }
        public DateTime? CompletedUtc { get; set; }
    }

    private sealed class LogEntryDto
    {
        public DateTime? Timestamp { get; set; }
        public string? SessionId { get; set; }
        public string? SessionName { get; set; }
        public string? AgentName { get; set; }
        public string? Severity { get; set; }
        public string? Message { get; set; }
    }

    private sealed class AgentOutputDto
    {
        public string? SessionId { get; set; }
        public string? AgentName { get; set; }
        public string? Line { get; set; }
        public string? Kind { get; set; }
    }
}
