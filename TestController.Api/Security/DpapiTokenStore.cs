using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// DPAPI-based token store that encrypts tokens at rest using Windows Data Protection.
/// Tokens are hashed with SHA-256 for comparison; the raw token is never stored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
    private readonly string _storePath;
    private readonly ILogger<DpapiTokenStore> _logger;
    private List<TokenEntry> _entries = new();
    private readonly object _lock = new();

    public DpapiTokenStore(IOptions<SecurityOptions> options, ILogger<DpapiTokenStore> logger)
    {
        _storePath = options.Value.Token.TokenStore;
        _logger = logger;
        LoadEntries();
    }

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
        // Generate a cryptographically random token (44 chars base64 = 32 bytes entropy)
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = HashToken(rawToken);
        var tokenId = Guid.NewGuid().ToString("N")[..12]; // Short unique ID

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

        lock (_lock)
        {
            _entries.Add(entry);
            SaveEntries();
        }

        _logger.LogInformation("Token provisioned: Id={TokenId}, Role={Role}, Agents=[{Agents}], Expires={Expires}",
            tokenId, role, string.Join(",", allowedAgents), expiresUtc?.ToString("o") ?? "never");

        return rawToken;
    }

    public bool RevokeToken(string tokenId)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e =>
                string.Equals(e.TokenId, tokenId, StringComparison.Ordinal));

            if (removed > 0)
            {
                SaveEntries();
                _logger.LogInformation("Token revoked: Id={TokenId}", tokenId);
                return true;
            }
        }

        _logger.LogWarning("Token revocation failed — not found: Id={TokenId}", tokenId);
        return false;
    }

    private void SaveEntries()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false });
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.LocalMachine);

            // Atomic write: write to temp file then rename
            var tempPath = _storePath + ".tmp";
            File.WriteAllBytes(tempPath, encrypted);
            File.Move(tempPath, _storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save token store to {Path}", _storePath);
        }
    }

    private void LoadEntries()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                _logger.LogInformation("Token store not found at {Path}, no tokens loaded", _storePath);
                return;
            }

            var encrypted = File.ReadAllBytes(_storePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            var json = Encoding.UTF8.GetString(decrypted);
            _entries = JsonSerializer.Deserialize<List<TokenEntry>>(json) ?? new();
            _logger.LogInformation("Loaded {Count} token(s) from store", _entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load token store from {Path}", _storePath);
            _entries = new();
        }
    }

    private static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }
}
