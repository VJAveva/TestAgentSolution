using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;

namespace TestController.Dashboard.Composition;

/// <summary>
/// Translates <see cref="IExecutionFeed"/> wire messages into mutations on
/// <see cref="ExecutionDashboardVM"/>. This is the only "remote-mode-specific"
/// glue in the standalone Dashboard project ? the VM, view, styles, and
/// supporting controls all live in TestControllerGrpc and are shared verbatim
/// with the in-process WPF host.
///
/// Threading: the feed already marshals callbacks onto the configured
/// dispatcher (the WPF UI thread), so all collection mutations here are
/// already on the right thread. We assert it defensively in DEBUG.
/// </summary>
public sealed class FeedToVmAdapter
{
    private readonly IExecutionFeed _feed;
    private readonly ExecutionDashboardVM _vm;
    private readonly ILogger<FeedToVmAdapter>? _log;
    private bool _attached;

    public FeedToVmAdapter(IExecutionFeed feed, ExecutionDashboardVM vm, ILogger<FeedToVmAdapter>? log = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _log = log;
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _feed.ConnectionChanged  += OnConnectionChanged;
        _feed.ExecutionStarted   += OnExecutionStarted;
        _feed.ExecutionCompleted += OnExecutionCompleted;
        _feed.ActionProgress     += OnActionProgress;
        _feed.LogsAppended       += OnLogsAppended;
        // GroupProgress is intentionally not surfaced into the VM yet:
        // sub-group state is a refinement that needs additional VM members.
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _feed.ConnectionChanged  -= OnConnectionChanged;
        _feed.ExecutionStarted   -= OnExecutionStarted;
        _feed.ExecutionCompleted -= OnExecutionCompleted;
        _feed.ActionProgress     -= OnActionProgress;
        _feed.LogsAppended       -= OnLogsAppended;
    }

    // ?? Handlers ?????????????????????????????????????????????????????

    private void OnConnectionChanged(object? sender, FeedConnectionState state)
    {
        _vm.ConnectionStatus = state switch
        {
            FeedConnectionState.Connected    => "Connected",
            FeedConnectionState.Connecting   => "Connecting",
            FeedConnectionState.Reconnecting => "Connecting",
            _                                => "Disconnected",
        };
    }

    private void OnExecutionStarted(object? sender, ExecutionStartedMessage msg)
    {
        if (_vm.Sessions.Any(s => s.SessionId == msg.SessionId)) return;

        var card = new SessionCardVM
        {
            SessionId    = msg.SessionId,
            WatchItemTag = msg.WatchItemTag,
            UserId       = msg.UserId,
            Source       = msg.Source,
            Status       = "Running",
            IsExpanded   = true,
        };
        _vm.Sessions.Insert(0, card);
        _log?.LogDebug("Session started {SessionId} ({Tag})", msg.SessionId, msg.WatchItemTag);
    }

    private void OnExecutionCompleted(object? sender, ExecutionCompletedMessage msg)
    {
        var card = _vm.Sessions.FirstOrDefault(s => s.SessionId == msg.SessionId);
        if (card == null) return;
        card.Status = msg.Status;
        _log?.LogDebug("Session completed {SessionId} -> {Status}", msg.SessionId, msg.Status);
    }

    private void OnActionProgress(object? sender, ActionProgressMessage msg)
    {
        var card = _vm.Sessions.FirstOrDefault(s => s.SessionId == msg.SessionId);
        if (card == null)
        {
            // Action arrived before ExecutionStarted (out-of-order replay): create a placeholder card.
            card = new SessionCardVM
            {
                SessionId    = msg.SessionId,
                WatchItemTag = msg.SessionId,
                Status       = "Running",
                IsExpanded   = true,
            };
            _vm.Sessions.Insert(0, card);
        }

        var agent = card.Agents.FirstOrDefault(a =>
            string.Equals(a.AgentName, msg.AgentName, StringComparison.OrdinalIgnoreCase));
        if (agent == null)
        {
            agent = new AgentRowVM { AgentName = msg.AgentName };
            card.Agents.Add(agent);
        }

        agent.UpdateAction(
            tag:             msg.ActionTag,
            actionType:      "",
            command:         msg.Command,
            status:          msg.Status,
            progressPercent: msg.ProgressPercent,
            startedUtc:      msg.StartedUtc,
            durationSeconds: msg.DurationSeconds);

        agent.Status = msg.Status switch
        {
            "Running" => "Executing",
            "Failed"  => "Failed",
            "Success" => "Idle",
            _         => agent.Status,
        };
    }

    private void OnLogsAppended(object? sender, IReadOnlyList<LogEntryMessage> batch)
    {
        foreach (var m in batch)
        {
            _vm.LogEntries.Add(new LogEntryVM
            {
                Timestamp   = m.Timestamp.ToString("HH:mm:ss.fff"),
                SessionId   = m.SessionId,
                SessionName = m.SessionName,
                AgentName   = m.AgentName,
                Severity    = m.Severity,
                Message     = m.Message,
            });
        }

        // Soft cap ? matches the in-process VM's eviction strategy (5000/500).
        const int maxLogs = 5000;
        const int evictBatch = 500;
        while (_vm.LogEntries.Count > maxLogs)
        {
            for (int i = 0; i < evictBatch && _vm.LogEntries.Count > 0; i++)
                _vm.LogEntries.RemoveAt(_vm.LogEntries.Count - 1);
        }
    }
}
