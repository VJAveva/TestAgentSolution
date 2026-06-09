using TestController.Api.Security;

namespace TestController.WebApi.Endpoints;

public static class TokenManagementEndpoints
{
    public static RouteGroupBuilder MapTokenManagementEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListTokens);
        group.MapGet("/{tokenId}", GetToken);
        group.MapPost("/", ProvisionToken);
        group.MapDelete("/{tokenId}", RevokeToken);
        return group;
    }

    /// <summary>GET /api/tokens — list all token entries (without secrets).</summary>
    private static IResult ListTokens(ITokenStore tokenStore)
    {
        var entries = tokenStore.GetEntries();
        var result = entries.Select(e => new TokenInfoDto
        {
            TokenId = e.TokenId,
            Role = e.Role,
            AllowedAgents = e.AllowedAgents,
            Description = e.Description,
            CreatedUtc = e.CreatedUtc,
            ExpiresUtc = e.ExpiresUtc,
            IsExpired = e.ExpiresUtc.HasValue && e.ExpiresUtc.Value < DateTime.UtcNow,
        });

        return Results.Ok(result);
    }

    /// <summary>GET /api/tokens/{tokenId} — get a single token entry info.</summary>
    private static IResult GetToken(string tokenId, ITokenStore tokenStore)
    {
        var entry = tokenStore.GetTokenById(tokenId);
        if (entry is null)
            return Results.NotFound(new { error = $"Token '{tokenId}' not found." });

        return Results.Ok(new TokenInfoDto
        {
            TokenId = entry.TokenId,
            Role = entry.Role,
            AllowedAgents = entry.AllowedAgents,
            Description = entry.Description,
            CreatedUtc = entry.CreatedUtc,
            ExpiresUtc = entry.ExpiresUtc,
            IsExpired = entry.ExpiresUtc.HasValue && entry.ExpiresUtc.Value < DateTime.UtcNow,
        });
    }

    /// <summary>POST /api/tokens — provision a new token (Admin only). Returns raw token once.</summary>
    private static IResult ProvisionToken(ProvisionTokenRequest request, ITokenStore tokenStore, ISecurityAuditLogger audit, HttpContext ctx)
    {
        // Validate request
        if (string.IsNullOrWhiteSpace(request.Role) ||
            (request.Role != "Admin" && request.Role != "User"))
        {
            return Results.BadRequest(new { error = "Role must be 'Admin' or 'User'." });
        }

        if (request.ExpiresInDays.HasValue && request.ExpiresInDays.Value <= 0)
        {
            return Results.BadRequest(new { error = "ExpiresInDays must be positive." });
        }

        var expiresUtc = request.ExpiresInDays.HasValue
            ? DateTime.UtcNow.AddDays(request.ExpiresInDays.Value)
            : (DateTime?)null;

        var rawToken = tokenStore.AddToken(
            request.Role,
            request.AllowedAgents ?? [],
            request.Description ?? "",
            expiresUtc);

        var entry = tokenStore.GetEntries().LastOrDefault(e => e.Description == (request.Description ?? ""));

        audit.LogAdminAction(ctx.User.Identity?.Name ?? "unknown", "TokenProvisioned",
            entry?.TokenId ?? "",
            $"Role={request.Role}, Agents=[{string.Join(",", request.AllowedAgents ?? [])}]");

        return Results.Created($"/api/tokens/{entry?.TokenId}", new ProvisionTokenResponse
        {
            TokenId = entry?.TokenId ?? "",
            RawToken = rawToken,
            Role = request.Role,
            AllowedAgents = request.AllowedAgents ?? [],
            ExpiresUtc = expiresUtc,
            Message = "Store this token securely — it cannot be retrieved again.",
        });
    }

    /// <summary>DELETE /api/tokens/{tokenId} — revoke a token (Admin only).</summary>
    private static IResult RevokeToken(string tokenId, ITokenStore tokenStore, ISecurityAuditLogger audit, HttpContext ctx)
    {
        var existing = tokenStore.GetTokenById(tokenId);
        if (existing is null)
            return Results.NotFound(new { error = $"Token '{tokenId}' not found." });

        var revoked = tokenStore.RevokeToken(tokenId);
        if (!revoked)
            return Results.Problem("Failed to revoke token.", statusCode: 500);

        audit.LogAdminAction(ctx.User.Identity?.Name ?? "unknown", "TokenRevoked",
            tokenId, $"Description={existing.Description}");

        return Results.Ok(new { message = $"Token '{tokenId}' revoked successfully." });
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────

public sealed class ProvisionTokenRequest
{
    public string Role { get; set; } = "User";
    public string[]? AllowedAgents { get; set; }
    public string? Description { get; set; }
    public int? ExpiresInDays { get; set; }
}

public sealed class ProvisionTokenResponse
{
    public string TokenId { get; set; } = "";
    public string RawToken { get; set; } = "";
    public string Role { get; set; } = "";
    public string[] AllowedAgents { get; set; } = [];
    public DateTime? ExpiresUtc { get; set; }
    public string Message { get; set; } = "";
}

public sealed class TokenInfoDto
{
    public string TokenId { get; set; } = "";
    public string Role { get; set; } = "";
    public string[] AllowedAgents { get; set; } = [];
    public string Description { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public bool IsExpired { get; set; }
}
