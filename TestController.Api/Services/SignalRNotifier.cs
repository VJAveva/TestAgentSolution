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
///   • C# events on <see cref="IActionPipelineExecutor"/> (LogEntry, NodeProgress, NodeFailed)
///   • C# events on <see cref="IAgentGrpcDispatcher"/> (OutputReceived, StatusChanged)
///   • C# events on <see cref="IVocabularyMonitor"/> (ConfigReloaded)
///   • <see cref="IEventAggregator"/> events (AgentRegistered, AgentUnregistered,
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
    private readonly CachedBuildResultsProvider _buildResults;
    private readonly ILogger<SignalRNotifier> _logger;

    private IDisposable? _subRegistered;
    private IDisposable? _subUnregistered;
    private IDisposable? _subHeartbeat;
    private IDisposable? _subExecutionStarted;
    private IDisposable? _subExecutionCompleted;
    private IDisposable? _subLocksChanged;

    // Heartbeat throttling
    private readonly object _heartbeatLock = new();
    private readonly Dictionary<string, object> _pendingHeartbeats = new();
    private Timer? _heartbeatTimer;

    public SignalRNotifier(
        IHubContext<ControllerHub> hub,
        IActionPipelineExecutor executor,
        IAgentGrpcDispatcher dispatcher,
        IVocabularyMonitor vocabMonitor,
        IEventAggregator events,
        CachedBuildResultsProvider buildResults,
        ILogger<SignalRNotifier> logger)
    {
        _hub = hub;
        _executor = executor;
        _dispatcher = dispatcher;
        _vocabMonitor = vocabMonitor;
        _events = events;
        _buildResults = buildResults;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to all event sources and starts the heartbeat timer.
    /// Call after the SignalR hub is mapped.
    /// </summary>
    public void Start()
    {
        // C# events from pipeline executor
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

        // Config reload
        _vocabMonitor.ConfigReloaded += OnWatchListReloaded;

        // Heartbeat flush timer (1 batch/second)
        _heartbeatTimer = new Timer(FlushHeartbeats, null, 1000, 1000);

        _logger.LogInformation(
            "SignalRNotifier started — subscribed to: LogEntry, NodeProgress, " +
            "OutputReceived, StatusChanged, AgentRegistered, AgentUnregistered, " +
            "Heartbeat, ExecutionStarted, ExecutionCompleted, ConfigReloaded");
    }

    // ?? C# event handlers ? IRealtimeNotifier calls ????????????????????

    private void OnLogEntry(PipelineLogEntry entry)
    {
        var sessionId = "";
        var message = entry.Message;
        if (message.StartsWith('[') && message.IndexOf(']') is > 0 and var endBracket)
        {
            sessionId = message[1..endBracket];
        }

        var severity = message.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("?", StringComparison.Ordinal)
            ? "Error"
            : message.Contains("Success", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("?", StringComparison.Ordinal)
              ? "Success"
              : message.Contains("?", StringComparison.Ordinal)
                    || message.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                ? "Warning"
                : "Info";

        var agentName = entry.Category == "Action" || entry.Category == "Retry"
            ? "Controller" : "";

        SendSafe("LogEntry", new
        {
            timestamp = entry.Timestamp.ToString("HH:mm:ss.fff"),
            sessionId,
            severity,
            category = entry.Category,
            agentName,
            message = entry.Message,
        });
    }

    private void OnNodeProgress(IActionNode node, string status)
    {
        if (node is ActionConfig action)
        {
            var agentName = string.IsNullOrEmpty(action.AgentName) ? "Controller" : action.AgentName;
            SendSafe("ActionProgress", new
            {
                actionTag = action.Order,
                actionType = action.Type.ToString(),
                agentName,
                command = action.Command,
                status,
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

    private void OnOutputReceived(string agentName, string line, string kind)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        SendSafe("AgentOutput", new
        {
            agentName,
            line,
            kind,
            timestamp = ts,
            sessionId = "",
            severity = kind == "stderr" ? "Error" : "Info",
            category = "Output",
            message = $"[{agentName}:{kind}] {line}",
        });
    }

    private void OnStatusChanged(string agentName, string status)
    {
        SendSafe("AgentStatusChanged", new
        {
            agentName,
            status,
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
        SendSafe("ExecutionStarted", new
        {
            sessionId = e.SessionId,
            watchItemTag = e.WatchItemTag,
            eventType = e.EventType,
            startTime = DateTime.UtcNow.ToString("o"),
            source = e.Source,
        });
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
        return _hub.Clients.Group("global").SendAsync("AgentLocksChanged", new
        {
            locks = e.Locks,
            reason = e.Reason,
            timestamp = e.Timestamp.ToString("o"),
        });
    }

    // ?? Transport ??????????????????????????????????????????????????????

    private void SendSafe(string method, object? arg)
    {
        _ = SendSafeAsync(method, arg);
    }

    private async Task SendSafeAsync(string method, object? arg)
    {
        try
        {
            if (arg is not null)
                await _hub.Clients.Group("global").SendAsync(method, arg);
            else
                await _hub.Clients.Group("global").SendAsync(method);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SignalR broadcast failed for {Method}", method);
        }
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
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
    }
}
