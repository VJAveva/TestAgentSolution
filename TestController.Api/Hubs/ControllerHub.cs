using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TestController.Api.Security;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Hubs;

/// <summary>
/// Shared SignalR hub for real-time communication with browser clients.
/// Used by both the WPF-hosted Kestrel server and the standalone WebApi.
/// Server pushes events; clients can join/leave session groups.
/// </summary>
[Authorize(Policy = SecurityPolicies.User)]
public sealed class ControllerHub : Hub
{
    private readonly ILogger<ControllerHub> _logger;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly ExecutionSessionManager _sessionManager;

    // Scale fix: Cache fleet snapshot for 2s to prevent O(agents×clients) rebuild.
    // Multiple browser tabs reconnecting simultaneously all call RequestFleetSnapshot,
    // generating N×200 LINQ traversals. Cache ensures only 1 build per 2 seconds.
    private static readonly object _snapshotLock = new();
    private static IReadOnlyList<AgentFleetGroupDto>? _cachedSnapshot;
    private static DateTime _snapshotExpiry = DateTime.MinValue;

    public ControllerHub(
        ILogger<ControllerHub> logger,
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        ExecutionSessionManager sessionManager)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _lockManager = lockManager;
        _sessionManager = sessionManager;
    }

    /// <summary>Subscribe to events for a specific session.</summary>
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionId}");
        _logger.LogDebug("[SignalR] {ConnectionId} joined session {SessionId}", Context.ConnectionId, sessionId);
    }

    /// <summary>Unsubscribe from a specific session.</summary>
    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session:{sessionId}");
    }

    /// <summary>Subscribe to all execution events.</summary>
    public async Task JoinAllSessions()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "all-sessions");
    }

    /// <summary>Subscribe to events for a specific user across all their sessions.</summary>
    public async Task JoinAsUser(string userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        _logger.LogDebug("[SignalR] {ConnectionId} joined user group {UserId}", Context.ConnectionId, userId);
    }

    /// <summary>Explicitly join the global group (auto-joined on connect, useful after reconnect).</summary>
    public async Task JoinGlobal()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "global");
    }

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "global");
        var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        _logger.LogInformation("[SignalR] Client connected: {ConnectionId} from {RemoteIp}",
            Context.ConnectionId, remoteIp);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[SignalR] Client disconnected: {ConnectionId}. Reason: {Reason}",
            Context.ConnectionId, exception?.Message ?? "clean");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client calls this on connect or reconnect to get the current
    /// fleet snapshot before applying incremental updates.
    /// Uses a 2-second cache to avoid redundant rebuilds when many clients connect.
    /// </summary>
    public IReadOnlyList<AgentFleetGroupDto> RequestFleetSnapshot()
    {
        lock (_snapshotLock)
        {
            if (_cachedSnapshot != null && DateTime.UtcNow < _snapshotExpiry)
                return _cachedSnapshot;

            _cachedSnapshot = BuildFleetSnapshot();
            _snapshotExpiry = DateTime.UtcNow.AddSeconds(2);
            return _cachedSnapshot;
        }
    }

    private IReadOnlyList<AgentFleetGroupDto> BuildFleetSnapshot()
    {
        var agents = _dispatcher.RegisteredAgents.ToList();
        var allHealth = _dispatcher.GetAllAgentHealth();
        var allLocks = _lockManager.GetAllLocks();

        var dtos = new List<AgentFleetDto>();
        foreach (var agentName in agents)
        {
            var dto = BuildAgentFleetDto(agentName, allHealth, allLocks);
            dtos.Add(dto);
        }

        var assigned = dtos
            .Where(a => a.GroupKey != null)
            .GroupBy(a => a.GroupKey!)
            .Select(g => new AgentFleetGroupDto(
                GroupKey: g.Key,
                Title: g.First().CurrentTestSet ?? g.Key.Replace("_", " "),
                Owner: g.First().Owner,
                IsAvailablePool: false,
                Agents: g.OrderBy(a => a.AgentId).ToList()))
            .OrderBy(g => g.Title)
            .ToList();

        var available = new AgentFleetGroupDto(
            GroupKey: "Available",
            Title: "Available",
            Owner: null,
            IsAvailablePool: true,
            Agents: dtos.Where(a => a.GroupKey == null).OrderBy(a => a.AgentId).ToList());

        return assigned.Concat(new[] { available }).ToList();
    }

    private AgentFleetDto BuildAgentFleetDto(
        string agentName,
        IReadOnlyDictionary<string, AgentHealthState> allHealth,
        IReadOnlyList<AgentLockManager.AgentLock> allLocks)
    {
        allHealth.TryGetValue(agentName, out var health);
        var agentLock = allLocks.FirstOrDefault(l =>
            string.Equals(l.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

        // Offline
        if (health != null && !health.IsHealthy && agentLock == null)
        {
            return new AgentFleetDto(
                AgentId: agentName, DisplayName: agentName,
                Status: AgentFleetStatus.Offline,
                CurrentTestSet: null, ProgressPercent: null,
                CompletedCount: null, TotalCount: null,
                StatusMessage: "Offline", GroupKey: null, Owner: null);
        }

        // Idle (no lock)
        if (agentLock == null)
        {
            return new AgentFleetDto(
                AgentId: agentName, DisplayName: agentName,
                Status: AgentFleetStatus.Idle,
                CurrentTestSet: null, ProgressPercent: null,
                CompletedCount: null, TotalCount: null,
                StatusMessage: null, GroupKey: null, Owner: null);
        }

        // Locked — derive state from session
        var session = _sessionManager.GetSession(agentLock.SessionId);
        var summary = session?.GetAgentSummaries()
            .FirstOrDefault(s => string.Equals(s.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

        var status = AgentFleetStatus.Waiting;
        string? statusMessage = null;
        string? currentTestSet = agentLock.WatchItemTag;
        int? progressPercent = null;
        int? completed = null;
        int? total = null;

        if (summary != null)
        {
            var hasFailed = summary.Actions.Any(a => a.Outcome == ActionOutcome.Failed);
            var running = summary.Actions.FirstOrDefault(a => a.Outcome == ActionOutcome.Unknown);

            if (hasFailed)
            {
                status = AgentFleetStatus.InstallFail;
                statusMessage = "Install fail";
            }
            else if (running != null)
            {
                status = AgentFleetStatus.Running;
                completed = summary.CompletedCount;
                total = summary.TotalCount;
                statusMessage = $"{completed}/{total} actions";
            }
        }

        return new AgentFleetDto(
            AgentId: agentName, DisplayName: agentName,
            Status: status,
            CurrentTestSet: currentTestSet,
            ProgressPercent: progressPercent,
            CompletedCount: completed, TotalCount: total,
            StatusMessage: statusMessage,
            GroupKey: agentLock.SessionId,
            Owner: agentLock.UserId);
    }
}
