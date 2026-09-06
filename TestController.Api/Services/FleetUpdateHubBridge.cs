using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using TestController.Api.Hubs;
using TestControllerGrpc.Core.Maintenance;

namespace TestController.Api.Services;

/// <summary>
/// Projects Windows Update posture onto the SignalR hub as <c>updateStatusChanged</c> and
/// <c>fleetNotificationRaised</c> (spec R24). No-ops on a host without the update stack.
/// </summary>
public sealed class FleetUpdateHubBridge : IHostedService
{
    private readonly IHubContext<ControllerHub> _hub;
    private readonly INodeUpdateStatusStore? _statuses;
    private readonly IFleetNotificationService? _notifications;
    private Guid _lastNotificationId;

    public FleetUpdateHubBridge(
        IHubContext<ControllerHub> hub,
        INodeUpdateStatusStore? statuses = null,
        IFleetNotificationService? notifications = null)
    {
        _hub = hub;
        _statuses = statuses;
        _notifications = notifications;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_statuses is not null) _statuses.Changed += OnStatusChanged;
        if (_notifications is not null) _notifications.NotificationsChanged += OnNotificationsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_statuses is not null) _statuses.Changed -= OnStatusChanged;
        if (_notifications is not null) _notifications.NotificationsChanged -= OnNotificationsChanged;
        return Task.CompletedTask;
    }

    private void OnStatusChanged(object? sender, NodeUpdateStatusChanged e)
    {
        var status = _statuses?.Get(e.NodeId);
        if (status is null) return;

        _ = _hub.Clients.All.SendAsync("updateStatusChanged", new
        {
            previous = e.Previous.ToString(),
            current = e.Current.ToString(),
            status = FleetUpdateDto.From(status),
        });
    }

    // The feed exposes newest-first; only the head can be new, so one id is enough to de-duplicate.
    private void OnNotificationsChanged(object? sender, EventArgs e)
    {
        var latest = _notifications?.Notifications.FirstOrDefault();
        if (latest is null || latest.Id == _lastNotificationId) return;
        _lastNotificationId = latest.Id;

        _ = _hub.Clients.All.SendAsync("fleetNotificationRaised", FleetUpdateDto.From(latest));
    }
}

/// <summary>Wire shapes for update posture — enums as strings so the React client never maps ordinals.</summary>
public static class FleetUpdateDto
{
    public static object From(NodeUpdateStatus s) => new
    {
        nodeId = s.NodeId,
        state = s.State.ToString(),
        lastSource = s.LastSource.ToString(),
        lastEventUtc = s.LastEventUtc,
        lastReportUtc = s.LastReportUtc,
        lastInstallUtc = s.LastInstallUtc,
        pendingCount = s.PendingCount,
        suppressedUntilUtc = s.SuppressedUntilUtc,
        snoozedUntilUtc = s.SnoozedUntilUtc,
        acknowledged = s.Acknowledged,
        items = s.Items.Select(i => new { kbId = i.KbId, title = i.Title, result = i.Result, resultCode = i.ResultCode }),
    };

    public static object From(FleetNotification n) => new
    {
        id = n.Id,
        nodeId = n.NodeId,
        kind = n.Kind.ToString(),
        title = n.Title,
        description = n.Description,
        source = n.Source.ToString(),
        detectedUtc = n.DetectedUtc,
        acknowledged = n.Acknowledged,
        isSuppressed = n.IsSuppressed,
    };

    public static object From(UpdatePolicy p) => new
    {
        pendingEffect = p.PendingEffect.ToString(),
        installingEffect = p.InstallingEffect.ToString(),
        rebootRequiredEffect = p.RebootRequiredEffect.ToString(),
        autoReboot = p.AutoReboot,
        autoRebootWindowStart = p.AutoRebootWindowStart?.ToString("HH:mm"),
        autoRebootWindowEnd = p.AutoRebootWindowEnd?.ToString("HH:mm"),
        registryPollSeconds = (int)p.RegistryPollInterval.TotalSeconds,
        suppressionWindowSeconds = (int)p.SuppressionWindow.TotalSeconds,
        coalescingWindowSeconds = (int)p.CoalescingWindow.TotalSeconds,
        maxConcurrentReboots = p.MaxConcurrentReboots,
    };
}
