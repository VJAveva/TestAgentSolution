using System.Security.Cryptography;

namespace TestController.Api.Security;

/// <summary>
/// In-memory token store used when AuthMode is not Token.
/// Supports token provisioning/revocation without DPAPI persistence.
/// Tokens are lost on restart — suitable for non-Token modes where the
/// token management endpoints are still accessible to admins.
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly List<TokenEntry> _entries = new();
    private readonly object _lock = new();

    public TokenEntry? ValidateToken(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        var hash = HashToken(rawToken);
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e =>
                string.Equals(e.HashedToken, hash, StringComparison.Ordinal));
            if (entry is null) return null;
            if (entry.ExpiresUtc.HasValue && entry.ExpiresUtc.Value < DateTime.UtcNow) return null;
            return entry;
        }
    }

    public IReadOnlyList<TokenEntry> GetEntries()
    {
        lock (_lock) return _entries.ToList();
    }

    public TokenEntry? GetTokenById(string tokenId)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e =>
                string.Equals(e.TokenId, tokenId, StringComparison.Ordinal));
        }
    }

    public string AddToken(string role, string[] allowedAgents, string description, DateTime? expiresUtc)
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = HashToken(rawToken);
        var tokenId = Guid.NewGuid().ToString("N")[..12];

        var entry = new TokenEntry
        {
            TokenId = tokenId,
            HashedToken = hash,
            Role = role,
            AllowedAgents = allowedAgents,
            Description = description,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = expiresUtc,
        };

        lock (_lock) { _entries.Add(entry); }
        return rawToken;
    }

    public bool RevokeToken(string tokenId)
    {
        lock (_lock)
        {
            return _entries.RemoveAll(e =>
                string.Equals(e.TokenId, tokenId, StringComparison.Ordinal)) > 0;
        }
    }

    private static string HashToken(string rawToken)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }
}
