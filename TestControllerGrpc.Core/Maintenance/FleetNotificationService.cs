namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// In-memory operator notification feed (spec §19). Thread-safe; raises <see cref="NotificationsChanged"/> on every
/// mutation so hosts (WPF dispatcher, SignalR) can re-project. Failed-update entries live here only — they never set
/// a card badge.
/// </summary>
public sealed class FleetNotificationService : IFleetNotificationService
{
    private const int MaxNotifications = 200;

    private readonly object _gate = new();
    private readonly List<FleetNotification> _notifications = new();

    public event EventHandler? NotificationsChanged;

    public IReadOnlyList<FleetNotification> Notifications
    {
        get { lock (_gate) return _notifications.ToList(); }
    }

    public void Raise(FleetNotification notification)
    {
        lock (_gate)
        {
            _notifications.Insert(0, notification);
            while (_notifications.Count > MaxNotifications)
                _notifications.RemoveAt(_notifications.Count - 1);
        }
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Acknowledge(Guid id)
    {
        var changed = false;
        lock (_gate)
        {
            var index = _notifications.FindIndex(n => n.Id == id);
            if (index >= 0 && !_notifications[index].Acknowledged)
            {
                _notifications[index] = _notifications[index] with { Acknowledged = true };
                changed = true;
            }
        }
        if (changed)
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Snooze(string nodeId, TimeSpan duration)
    {
        var changed = false;
        lock (_gate)
        {
            for (var i = 0; i < _notifications.Count; i++)
            {
                if (string.Equals(_notifications[i].NodeId, nodeId, StringComparison.OrdinalIgnoreCase) && !_notifications[i].Acknowledged)
                {
                    _notifications[i] = _notifications[i] with { Acknowledged = true };
                    changed = true;
                }
            }
        }
        if (changed)
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkAllRead()
    {
        var changed = false;
        lock (_gate)
        {
            for (var i = 0; i < _notifications.Count; i++)
            {
                if (!_notifications[i].Acknowledged)
                {
                    _notifications[i] = _notifications[i] with { Acknowledged = true };
                    changed = true;
                }
            }
        }
        if (changed)
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }
}
