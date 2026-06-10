namespace TestController.Persistence.Identity;

/// <summary>
/// bcrypt password hasher (cost factor 12) per 01_System_Design.md NFR-SEC-02.
/// </summary>
public sealed class PasswordHasher
{
    private const int WorkFactor = 12;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}
