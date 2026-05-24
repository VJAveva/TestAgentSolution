using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// Token mode authentication provider.
/// Uses pre-shared bearer tokens stored in an encrypted store.
/// Each token maps to a role and optionally a set of allowed agents.
/// </summary>
public sealed class TokenAuthModeProvider : IAuthenticationModeProvider
{
    private readonly SecurityOptions _options;
    private readonly ILogger<TokenAuthModeProvider> _logger;

    public TokenAuthModeProvider(IOptions<SecurityOptions> options, ILogger<TokenAuthModeProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public AuthMode Mode => AuthMode.Token;
    public bool IsHealthy => true;

    public UserRole ResolveRole(ClaimsPrincipal user)
    {
        if (user.Identity is null || !user.Identity.IsAuthenticated)
            return UserRole.Anonymous;

        // Token role is embedded as a claim during token validation
        var roleClaim = user.FindFirst(ClaimTypes.Role)?.Value;
        return roleClaim switch
        {
            "Admin" => UserRole.Admin,
            "User" => UserRole.User,
            _ => UserRole.User
        };
    }

    public string GetDiagnosticStatus() => $"Token mode active (store: {_options.Token.TokenStore})";
}

/// <summary>
/// Represents a pre-shared token entry in the token store.
/// </summary>
public sealed class TokenEntry
{
    public string TokenId { get; set; } = "";
    public string HashedToken { get; set; } = "";
    public string Role { get; set; } = "User";
    public string[] AllowedAgents { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>
/// ASP.NET Core authentication handler for bearer token validation.
/// </summary>
public sealed class TokenAuthenticationHandler : AuthenticationHandler<TokenAuthenticationOptions>
{
    private readonly ITokenStore _tokenStore;

    public TokenAuthenticationHandler(
        IOptionsMonitor<TokenAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITokenStore tokenStore)
        : base(options, logger, encoder)
    {
        _tokenStore = tokenStore;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var entry = _tokenStore.ValidateToken(token);
        if (entry is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired token"));

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, entry.TokenId),
            new(ClaimTypes.NameIdentifier, entry.TokenId),
            new(ClaimTypes.Role, entry.Role),
            new("AllowedAgents", string.Join(",", entry.AllowedAgents))
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class TokenAuthenticationOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Interface for the encrypted token store.
/// </summary>
public interface ITokenStore
{
    /// <summary>Validates a raw token string. Returns the entry if valid, null otherwise.</summary>
    TokenEntry? ValidateToken(string rawToken);

    /// <summary>Gets all token entries (without sensitive data).</summary>
    IReadOnlyList<TokenEntry> GetEntries();

    /// <summary>Provisions a new token. Returns the raw token string (shown once).</summary>
    string AddToken(string role, string[] allowedAgents, string description, DateTime? expiresUtc);

    /// <summary>Revokes a token by its ID. Returns true if found and removed.</summary>
    bool RevokeToken(string tokenId);

    /// <summary>Gets a token entry by its ID (without the raw token).</summary>
    TokenEntry? GetTokenById(string tokenId);
}
