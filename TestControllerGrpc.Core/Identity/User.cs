using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Identity;

/// <summary>
/// POCO entity for the Users table. Per 01_System_Design.md §7.1.
/// </summary>
public sealed class User
{
    public string UserId { get; set; } = Guid.NewGuid().ToString("D").ToLowerInvariant();
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public Role Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
}
