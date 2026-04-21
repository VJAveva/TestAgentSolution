using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Hubs;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Bridges existing service events to SignalR broadcasts.
/// Subscribes to ActionPipelineExecutor.NodeProgress, LogEntry,
/// AgentGrpcDispatcher.OutputReceived, StatusChanged, and
/// EventAggregator events (agent registration, heartbeat),
/// then pushes them to all connected WebSocket clients.
///
/// Heartbeats are throttled to 1 broadcast per second to prevent
/// flooding when many agents are connected.
/// </summary>
public sealed class SignalRBridge : IDisposable
{
    private readonly IHubContext<ControllerHub> _hub;
    private readonly IActionPipelineExecutor _executor;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IEventAggregator _events;
    private readonly ILogger<SignalRBridge> _logger;

    private IDisposable? _subRegistered;
    private IDisposable? _subUnregistered;
    private IDisposable? _subHeartbeat;

    // Heartbeat throttling: coalesce rapid heartbeats into a single push per second
    private readonly object _heartbeatLock = new();
    private readonly Dictionary<string, object> _pendingHeartbeats = new();
    private Timer? _heartbeatTimer;

    public SignalRBridge(
        IHubContext<ControllerHub> hub,
        IActionPipelineExecutor executor,
        IAgentGrpcDispatcher dispatcher,
        IVocabularyMonitor vocabMonitor,
        IEventAggregator events,
        ILogger<SignalRBridge> logger)
    {
        _hub = hub;
        _executor = executor;
        _dispatcher = dispatcher;
        _vocabMonitor = vocabMonitor;
        _events = events;
        _logger = logger;
    }

    public void Start()
    {
        _executor.LogEntry += OnLogEntry;
        _executor.NodeProgress += OnNodeProgress;

        _dispatcher.OutputReceived += OnOutputReceived;
        _dispatcher.StatusChanged += OnStatusChanged;

        _subRegistered = _events.Subscribe<AgentRegisteredEvent>(OnAgentRegistered);
        _subUnregistered = _events.Subscribe<AgentUnregisteredEvent>(OnAgentUnregistered);
        _subHeartbeat = _events.Subscribe<AgentHeartbeatEvent>(OnHeartbeat);

        _vocabMonitor.ConfigReloaded += OnWatchListReloaded;

        // Flush pending heartbeats every 1 second
        _heartbeatTimer = new Timer(FlushHeartbeats, null, 1000, 1000);

        _logger.LogInformation(
            "SignalR bridge started. Subscribed to: LogEntry, NodeProgress, " +
            "OutputReceived, StatusChanged, AgentRegistered, AgentUnregistered, " +
            "Heartbeat, ConfigReloaded");
    }

    private void OnLogEntry(PipelineLogEntry entry)
    {
        // Extract sessionId from message prefix pattern: [sessionId] ...
        var sessionId = "";
        var message = entry.Message;
        if (message.StartsWith('[') && message.IndexOf(']') is > 0 and var endBracket)
        {
            sessionId = message[1..endBracket];
        }

        // Infer severity from message content
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

        // Infer agent name from category when available
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
            SendSafe("GroupProgress", new
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
        // Broadcast as AgentOutput for execution monitor
        SendSafe("AgentOutput", new
        {
            agentName,
            line,
            kind,
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
        });

        // Also broadcast as LogEntry for the unified log viewer
        SendSafe("LogEntry", new
        {
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            sessionId = "",
            severity = kind == "stderr" ? "Error" : "Info",
            category = "Output",
            agentName,
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

    /// <summary>
    /// Heartbeats are coalesced per-agent and flushed once per second
    /// to avoid flooding WebSocket connections when many agents are connected.
    /// </summary>
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

    /// <summary>
    /// Sends a SignalR message to the "global" group with error logging.
    /// Prevents unobserved task exceptions from fire-and-forget calls.
    /// </summary>
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
    }
}
