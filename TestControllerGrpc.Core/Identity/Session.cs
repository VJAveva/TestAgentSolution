namespace TestControllerGrpc.Identity;

/// <summary>
/// POCO entity for the Sessions table. Per 01_System_Design.md §7.1.
/// </summary>
public sealed class Session
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("D").ToLowerInvariant();
    public string? UserId { get; set; }
    public string? GuestId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public ClientKind ClientKind { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedUtc { get; set; }
}
