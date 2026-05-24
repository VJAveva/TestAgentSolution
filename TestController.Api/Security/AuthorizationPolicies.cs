using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace TestController.Api.Security;

/// <summary>
/// Named policy constants for the multi-identity security framework.
/// </summary>
public static class SecurityPolicies
{
    /// <summary>Requires membership in the admin group.</summary>
    public const string Admin = "Admin";

    /// <summary>Requires any authenticated user (admin or standard user).</summary>
    public const string User = "User";

    /// <summary>Allows unauthenticated access (health endpoints only).</summary>
    public const string Anonymous = "Anonymous";
}

/// <summary>
/// Authorization requirement: user must have Admin role as resolved by the active mode provider.
/// </summary>
public sealed class AdminRoleRequirement : IAuthorizationRequirement { }

/// <summary>
/// Evaluates the Admin requirement using the active <see cref="IAuthenticationModeProvider"/>.
/// </summary>
public sealed class AdminRoleHandler : AuthorizationHandler<AdminRoleRequirement>
{
    private readonly IAuthenticationModeProvider _modeProvider;

    public AdminRoleHandler(IAuthenticationModeProvider modeProvider)
    {
        _modeProvider = modeProvider;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRoleRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        var role = _modeProvider.ResolveRole(context.User);
        if (role == UserRole.Admin)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Returns RFC 7807 problem+json for authorization failures instead of a bare 403/401.
/// For Negotiate authentication challenges, delegates to the default handler so the
/// WWW-Authenticate header is properly emitted for Kerberos/NTLM handshake.
/// </summary>
public sealed class ProblemDetailsAuthorizationHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            var problem = new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                title = "Forbidden",
                status = 403,
                detail = "You do not have permission to perform this action.",
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
            return;
        }

        if (authorizeResult.Challenged)
        {
            // For Negotiate (Kerberos/NTLM) auth, the browser needs a bare 401 with
            // WWW-Authenticate header to initiate the handshake. Only write problem+json
            // for API clients that already attempted authentication (have Authorization header).
            if (!context.Request.Headers.ContainsKey("Authorization"))
            {
                // Let the default handler emit the WWW-Authenticate challenge
                await _default.HandleAsync(next, context, policy, authorizeResult);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            var problem = new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                title = "Unauthorized",
                status = 401,
                detail = "Authentication is required.",
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
