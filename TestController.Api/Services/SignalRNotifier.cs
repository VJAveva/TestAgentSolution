using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TestController.Api.Hubs;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Services;

/// <summary>
/// Unified IRealtimeNotifier implementation that broadcasts all events
/// to a single <see cref="ControllerHub"/> via <see cref="IHubContext{THub}"/>.
///
/// Subscribes to:
///   � C# events on <see cref="IActionPipelineExecutor"/> (LogEntry, NodeProgress, NodeFailed)
///   � C# events on <see cref="IAgentGrpcDispatcher"/> (OutputReceived, StatusChanged)
///   � C# events on <see cref="IVocabularyMonitor"/> (ConfigReloaded)
///   � <see cref="IEventAggregator"/> events (AgentRegistered, AgentUnregistered,
///     AgentHeartbeat, ExecutionStarted, ExecutionCompleted)
///
/// Includes heartbeat throttling: coalesces rapid heartbeats into a single
/// batch push per second to prevent flooding when many agents are connected.
///
/// Replaces the former dual-hub design (SignalRBridge + LiveHub) with a
/// single hub at <c>/hubs/controller</c>.
/// </summary>
public sealed class SignalRNotifier : IRealtimeNotifier, IDisposable
{
    private readonly IHubContext<ControllerHub> _hub;
    private readonly IActionPipelineExecutor _executor;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IEventAggregator _events;
    private readonly ExecutionSessionManager _sessionManager;
    private readonly CachedBuildResultsProvider _buildResults;
    private readonly ILogger<SignalRNotifier> _logger;

    private IDisposable? _subRegistered;
    private IDisposable? _subUnregistered;
    private IDisposable? _subHeartbeat;
    private IDisposable? _subExecutionStarted;
    private IDisposable? _subExecutionCompleted;
    private IDisposable? _subLocksChanged;
    // Session-aware progress/output subscriptions (carry SessionId so the
    // WebClient/standalone dashboard can route messages to the right card).
    private IDisposable? _subNodeProgress;
    private IDisposable? _subAgentOutput;

    // Heartbeat throttling
    private readonly object _heartbeatLock = new();
    private readonly Dictionary<string, object> _pendingHeartbeats = new();
    private Timer? _heartbeatTimer;

    // Output batching: coalesces rapid output lines into batch pushes (500ms)
    private readonly object _outputLock = new();
    private readonly List<object> _pendingOutputLines = new();
    private Timer? _outputTimer;

    public SignalRNotifier(
        IHubContext<ControllerHub> hub,
        IActionPipelineExecutor executor,
        IAgentGrpcDispatcher dispatcher,
        IVocabularyMonitor vocabMonitor,
        IEventAggregator events,
        ExecutionSessionManager sessionManager,
        CachedBuildResultsProvider buildResults,
        ILogger<SignalRNotifier> logger)
    {
        _hub = hub;
        _executor = executor;
        _dispatcher = dispatcher;
        _vocabMonitor = vocabMonitor;
        _events = events;
        _sessionManager = sessionManager;
        _buildResults = buildResults;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to all event sources and starts the heartbeat timer.
    /// Call after the SignalR hub is mapped.
    /// </summary>
    public void Start()
    {
        // C# events from pipeline executor (kept for back-compat / non-tracked
        // single-action runs). Session-aware ActionProgress/AgentOutput now
        // come from the EventAggregator subscriptions below, so the WebClient
        // can route messages to the correct session card.
        _executor.LogEntry += OnLogEntry;
        _executor.NodeProgress += OnNodeProgress;

        // C# events from agent dispatcher
        _dispatcher.OutputReceived += OnOutputReceived;
        _dispatcher.StatusChanged += OnStatusChanged;

        // IEventAggregator events
        _subRegistered = _events.Subscribe<AgentRegisteredEvent>(OnAgentRegistered);
        _subUnregistered = _events.Subscribe<AgentUnregisteredEvent>(OnAgentUnregistered);
        _subHeartbeat = _events.Subscribe<AgentHeartbeatEvent>(OnHeartbeat);
        _subExecutionStarted = _events.Subscribe<ExecutionStartedEvent>(OnExecutionStarted);
        _subExecutionCompleted = _events.Subscribe<ExecutionCompletedEvent>(OnExecutionCompleted);
        _subLocksChanged = _events.Subscribe<AgentLocksChangedEvent>(e => _ = NotifyAgentLocksChanged(e));
        // ? These carry SessionId � fixes "Pipeline view not updating" because
        //   the WebClient looks up the card by sessionId on every ActionProgress.
        _subNodeProgress = _events.Subscribe<NodeProgressEvent>(OnNodeProgressEvent);
        _subAgentOutput = _events.Subscribe<AgentOutputEvent>(OnAgentOutputEvent);

        // Config reload
        _vocabMonitor.ConfigReloaded += OnWatchListReloaded;

        // Heartbeat flush timer (1 batch/second)
        _heartbeatTimer = new Timer(FlushHeartbeats, null, 1000, 1000);

        // Output flush timer (batch every 500ms for near-realtime display)
        _outputTimer = new Timer(FlushOutput, null, 500, 500);

        _logger.LogInformation(
            "SignalRNotifier started � subscribed to: LogEntry, NodeProgress, " +
            "OutputReceived, StatusChanged, AgentRegistered, AgentUnregistered, " +
            "Heartbeat, ExecutionStarted, ExecutionCompleted, ConfigReloaded, " +
            "NodeProgressEvent (with SessionId), AgentOutputEvent (with SessionId)");
    }

    // ?? C# event handlers ? IRealtimeNotifier calls ????????????????????

    private void OnLogEntry(PipelineLogEntry entry)
    {
        // Prefer the structured SessionId on the entry; fall back to parsing
        // the legacy "[<sessionId>] message" prefix if older callers don't
        // populate it. This is critical for the WebClient to filter logs
        // per session.
        var sessionId = entry.SessionId ?? "";
        var message = entry.Message;
        if (string.IsNullOrEmpty(sessionId)
            && message.StartsWith('[') && message.IndexOf(']') is > 0 and var endBracket)
        {
            sessionId = message[1..endBracket];
        }

        var severity = entry.Severity
            ?? (message.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("?", StringComparison.Ordinal)
                ? "Error"
                : message.Contains("Success", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("?", StringComparison.Ordinal)
                  ? "Success"
                  : message.Contains("?", StringComparison.Ordinal)
                        || message.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                    ? "Warning"
                    : "Info");

        var agentName = entry.AgentName
            ?? (entry.Category == "Action" || entry.Category == "Retry"
                ? "Controller" : "");

        SendSafe("LogEntry", new
        {
            timestamp = entry.Timestamp.ToString("HH:mm:ss.fff"),
            sessionId,
            severity,
            category = entry.Category,
            agentName,
            message = SecurityRedactor.Redact(entry.Message),
        });
    }

    private void OnNodeProgress(IActionNode node, string status)
    {
        if (node is ActionConfig action)
        {
            var agentName = string.IsNullOrEmpty(action.AgentName) ? "Controller" : action.AgentName;
            SendSafe("ActionProgress", new
            {
                actionTag = action.ResolvedTag,
                actionType = action.Type.ToString(),
                agentName,
                command = SecurityRedactor.Redact(action.Command),
                status = SecurityRedactor.Redact(status),
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            });
        }
        else if (node is ActionGroupConfig group)
        {
            SendSafe("ActionProgress", new
            {
                groupTag = group.Tag,
                executionType = group.ExecutionType.ToString(),
                status,
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            });
        }
    }

    /// <summary>
    /// Session-aware action progress (preferred). Fired by
    /// <c>ExecutionSessionManager.BeginAction</c> / <c>RecordResult</c>; carries
    /// the SessionId so the WebClient/standalone Dashboard can route the
    /// message to the correct session card. The legacy
    /// <see cref="OnNodeProgress"/> handler above still fires for non-tracked
    /// runs (single-action / ad-hoc trigger).
    /// </summary>
    private void OnNodeProgressEvent(NodeProgressEvent e)
    {
        SendSafe("ActionProgress", new
        {
            sessionId       = e.SessionId,
            agentName       = e.AgentName,
            actionTag       = e.NodeTag,
            actionType      = e.ActionType,
            command         = SecurityRedactor.Redact(e.Command),
            status          = SecurityRedactor.Redact(e.Status),
            exitCode        = e.ExitCode,
            errorMessage    = SecurityRedactor.Redact(e.ErrorMessage),
            duration        = e.Duration,
            progressPercent = e.ProgressPercent,
            timestamp       = DateTime.Now.ToString("HH:mm:ss.fff"),
        });
    }

    /// <summary>
    /// Session-aware agent stdout/stderr (preferred over <see cref="OnOutputReceived"/>).
    /// Carries SessionId + Kind so the dashboard can colour-code and filter.
    /// </summary>
    private void OnAgentOutputEvent(AgentOutputEvent e)
    {
        var severity = string.Equals(e.Kind, "stderr", StringComparison.OrdinalIgnoreCase)
            ? "Error" : "Info";
        var payload = new
        {
            sessionId = e.SessionId,
            agentName = e.AgentName,
            line      = SecurityRedactor.Redact(e.Line),
            kind      = e.Kind,
            timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
            severity,
            category  = "Output",
            message   = $"[{e.AgentName}:{e.Kind}] {SecurityRedactor.Redact(e.Line)}",
        };

        // Scale fix: Buffer output lines and flush every 500ms.
        // At 200 agents producing output, this reduces hundreds of individual
        // SignalR broadcasts per second down to 2 batch calls.
        lock (_outputLock)
        {
            _pendingOutputLines.Add(payload);
        }
    }

    private void FlushOutput(object? state)
    {
        List<object> batch;
        lock (_outputLock)
        {
            if (_pendingOutputLines.Count == 0) return;
            batch = new List<object>(_pendingOutputLines);
            _pendingOutputLines.Clear();
        }

        SendSafe("AgentOutputBatch", batch);
    }

    private void OnOutputReceived(string agentName, string line, string kind)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        SendSafe("AgentOutput", new
        {
            agentName,
            line = SecurityRedactor.Redact(line),
            kind,
            timestamp = ts,
            sessionId = "",
            severity = kind == "stderr" ? "Error" : "Info",
            category = "Output",
            message = $"[{agentName}:{kind}] {SecurityRedactor.Redact(line)}",
        });
    }

    private void OnStatusChanged(string agentName, string status)
    {
        SendSafe("AgentStatusChanged", new
        {
            agentName,
            status = SecurityRedactor.Redact(status),
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        });
    }

    private void OnAgentRegistered(AgentRegisteredEvent e)
    {
        SendSafe("AgentRegistered", new
        {
            agentName = e.AgentName,
            address = e.Address,
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        });
    }

    private void OnAgentUnregistered(AgentUnregisteredEvent e)
    {
        SendSafe("AgentUnregistered", new
        {
            agentName = e.AgentName,
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        });
    }

    private void OnHeartbeat(AgentHeartbeatEvent e)
    {
        var payload = new
        {
            agentName = e.AgentName,
            state = e.State.ToString(),
            cpuUsagePct = e.Metrics?.CpuUsagePct ?? 0,
            memoryUsedMb = e.Metrics?.MemoryUsedMb ?? 0,
            memoryTotalMb = e.Metrics?.MemoryTotalMb ?? 0,
            diskFreeGb = e.Metrics?.DiskFreeGb ?? 0,
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        };

        lock (_heartbeatLock)
        {
            _pendingHeartbeats[e.AgentName] = payload;
        }
    }

    private void FlushHeartbeats(object? state)
    {
        List<object> batch;
        lock (_heartbeatLock)
        {
            if (_pendingHeartbeats.Count == 0) return;
            batch = _pendingHeartbeats.Values.ToList();
            _pendingHeartbeats.Clear();
        }

        SendSafe("AgentHeartbeats", batch);
    }

    private void OnWatchListReloaded(WatchListConfig config)
    {
        SendSafe("WatchListReloaded", null);
    }

    private void OnExecutionStarted(ExecutionStartedEvent e)
    {
        // Include pending actions from the snapshot tree so the WebClient can
        // pre-populate all pills as "Pending" (user sees the full pipeline scope)
        var session = _sessionManager.GetSession(e.SessionId);
        var pendingActions = session?.SnapshotNodes?.Count > 0
            ? ExtractPendingActions(session.SnapshotNodes, session.ResolvedParameters)
            : Array.Empty<object>();

        SendSafe("ExecutionStarted", new
        {
            sessionId = e.SessionId,
            watchItemTag = e.WatchItemTag,
            eventType = e.EventType,
            startTime = DateTime.UtcNow.ToString("o"),
            source = e.Source,
            userId = session?.UserId ?? "",
            lockedAgents = session?.LockedAgents ?? Array.Empty<string>(),
            pendingActions,
        });
    }

    /// <summary>Walks the snapshot tree and emits flat action descriptors for the WebClient.</summary>
    private static object[] ExtractPendingActions(
        IReadOnlyList<IActionNode> nodes, Dictionary<string, string>? parameters)
    {
        var result = new List<object>();
        CollectActions(nodes, parameters, result);
        return result.ToArray();
    }

    private static void CollectActions(
        IReadOnlyList<IActionNode> nodes, Dictionary<string, string>? parameters, List<object> result)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ActionConfig action:
                    var agent = ResolveAgent(action.AgentName, parameters);
                    result.Add(new
                    {
                        tag = action.ResolvedTag,
                        actionType = action.Type.ToString(),
                        agentName = agent,
                        command = SecurityRedactor.Redact(action.Command),
                        status = "Pending",
                    });
                    break;

                case ActionGroupConfig group:
                    CollectActions(group.Children, parameters, result);
                    break;
            }
        }
    }

    private static string ResolveAgent(string agentName, Dictionary<string, string>? parameters)
    {
        if (string.IsNullOrEmpty(agentName)) return "Controller";
        if (parameters != null && agentName.StartsWith('[') && agentName.EndsWith(']'))
        {
            var varName = agentName[1..^1];
            if (parameters.TryGetValue(varName, out var resolved)) return resolved;
            if (varName.StartsWith('_') && parameters.TryGetValue(varName[1..], out resolved)) return resolved;
        }
        return agentName;
    }

    private void OnExecutionCompleted(ExecutionCompletedEvent e)
    {
        // Invalidate build results cache so the next dashboard load picks up new TRX files
        _buildResults.InvalidateAll();

        SendSafe("ExecutionCompleted", new
        {
            sessionId = e.SessionId,
            watchItemTag = e.WatchItemTag,
            state = e.State,
            passed = e.Passed,
            failed = e.Failed,
            total = e.Total,
            timestamp = DateTime.UtcNow.ToString("o"),
        });

        // Notify Results page to refresh build list
        SendSafe("ResultsUpdated", new
        {
            watchItemTag = e.WatchItemTag,
            timestamp = DateTime.UtcNow.ToString("o"),
        });
    }

    // ?? IRealtimeNotifier (for direct calls from services) ?????????????

    public Task NotifyLogEntry(PipelineLogEntry entry) { OnLogEntry(entry); return Task.CompletedTask; }
    public Task NotifyActionProgress(object payload) => SendSafeAsync("ActionProgress", payload);
    public Task NotifyAgentOutput(object payload) => SendSafeAsync("AgentOutput", payload);
    public Task NotifyAgentStatusChanged(object payload) => SendSafeAsync("AgentStatusChanged", payload);
    public Task NotifyAgentHeartbeats(IReadOnlyList<object> batch) => SendSafeAsync("AgentHeartbeats", batch);
    public Task NotifyExecutionStarted(ExecutionStartedEvent e) { OnExecutionStarted(e); return Task.CompletedTask; }
    public Task NotifyExecutionCompleted(ExecutionCompletedEvent e) { OnExecutionCompleted(e); return Task.CompletedTask; }
    public Task NotifyAgentRegistered(AgentRegisteredEvent e) { OnAgentRegistered(e); return Task.CompletedTask; }
    public Task NotifyAgentUnregistered(AgentUnregisteredEvent e) { OnAgentUnregistered(e); return Task.CompletedTask; }
    public Task NotifyWatchListReloaded() { SendSafe("WatchListReloaded", null); return Task.CompletedTask; }
    public Task NotifyAgentLocksChanged(AgentLocksChangedEvent e)
    {
        // Lock changes go to all connected clients (everyone needs to update UI)
        return SendSafeAsync("AgentLocksChanged", new
        {
            locks = e.Locks,
            reason = e.Reason,
            timestamp = e.Timestamp.ToString("o"),
        });
    }

    // ── Fleet panel notifications ──────────────────────────────────────────────

    public Task NotifyFleetAgentUpdated(AgentFleetDto agent)
        => SendSafeAsync("FleetAgentUpdated", agent);

    public Task NotifyFleetAgentRemoved(string agentId)
        => SendSafeAsync("FleetAgentRemoved", agentId);

    public Task NotifyFleetSnapshot(IReadOnlyList<AgentFleetGroupDto> groups)
        => SendSafeAsync("FleetSnapshot", groups);

    // ── Transport ──────────────────────────────────────────────────────────────

    /// <summary>Broadcast timeout to prevent slow clients from blocking sends.</summary>
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(5);

    private void SendSafe(string method, object? arg)
    {
        _ = SendSafeAsync(method, arg);
    }

    private async Task SendSafeAsync(string method, object? arg)
    {
        try
        {
            using var cts = new CancellationTokenSource(BroadcastTimeout);
            if (arg is not null)
                await _hub.Clients.Group("global").SendAsync(method, arg, cts.Token);
            else
                await _hub.Clients.Group("global").SendAsync(method, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("SignalR broadcast timed out for {Method} after {Timeout}s",
                method, BroadcastTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SignalR broadcast failed for {Method}", method);
        }
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _outputTimer?.Dispose();
        _executor.LogEntry -= OnLogEntry;
        _executor.NodeProgress -= OnNodeProgress;
        _dispatcher.OutputReceived -= OnOutputReceived;
        _dispatcher.StatusChanged -= OnStatusChanged;
        _vocabMonitor.ConfigReloaded -= OnWatchListReloaded;
        _subRegistered?.Dispose();
        _subUnregistered?.Dispose();
        _subHeartbeat?.Dispose();
        _subExecutionStarted?.Dispose();
        _subExecutionCompleted?.Dispose();
        _subLocksChanged?.Dispose();
        _subNodeProgress?.Dispose();
        _subAgentOutput?.Dispose();
    }
}
