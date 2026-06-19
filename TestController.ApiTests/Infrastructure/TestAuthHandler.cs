using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestController.Api.Security;

namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// Authentication handler used in InMemory mode. The production pipeline uses
/// Negotiate/Windows auth, which cannot run under TestServer, and every execution
/// endpoint requires an authenticated user (SecurityPolicies.User =
/// RequireAuthenticatedUser, plus a fallback policy). Without this, every request
/// would 401. This handler always authenticates as "TestUser" with the role from
/// the optional X-Test-Role header (default Admin, so Admin-only routes work too).
/// Mirrors the proven handler in TestController.WebApi.Tests.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = "Admin";
        if (Request.Headers.TryGetValue("X-Test-Role", out var roleHeader) &&
            !string.IsNullOrWhiteSpace(roleHeader))
        {
            role = roleHeader.ToString();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user"),
            new Claim(ClaimTypes.Name, "TestUser"),
            new Claim(ClaimTypes.Role, role),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Resolves roles from standard <see cref="ClaimTypes.Role"/> claims so the shared
/// AdminRoleHandler works under the test auth scheme.
/// </summary>
internal sealed class TestAuthModeProvider : IAuthenticationModeProvider
{
    public AuthMode Mode => AuthMode.Domain;
    public bool IsHealthy => true;

    public UserRole ResolveRole(ClaimsPrincipal user)
    {
        if (user.IsInRole("Admin")) return UserRole.Admin;
        if (user.Identity?.IsAuthenticated == true) return UserRole.User;
        return UserRole.Anonymous;
    }

    public string GetDiagnosticStatus() => "Test mode: authenticated users resolve role from claims";
}
