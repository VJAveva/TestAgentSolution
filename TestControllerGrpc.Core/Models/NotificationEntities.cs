namespace TestControllerGrpc.Models;

/// <summary>
/// Persisted mute entry — suppresses automatic notifications for a pipeline tag or test name.
/// Stored in OrchestratorDbContext.NotificationMutes.
/// </summary>
public sealed class NotificationMute
{
    public long MuteId { get; set; }
    public string Target { get; set; } = "";          // Pipeline tag or test name
    public string TargetType { get; set; } = "Pipeline"; // "Pipeline" or "Test"
    public string MutedByUserId { get; set; } = "";
    public DateTime MutedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }       // null = permanent until unmuted
}

/// <summary>
/// Tracks the last time a notification was sent for a target, implementing cooldown logic.
/// Stored in OrchestratorDbContext.NotificationCooldowns.
/// </summary>
public sealed class NotificationCooldown
{
    public long CooldownId { get; set; }
    public string Target { get; set; } = "";          // Pipeline tag or test name
    public string TargetType { get; set; } = "Pipeline";
    public DateTime LastSentUtc { get; set; }
}
