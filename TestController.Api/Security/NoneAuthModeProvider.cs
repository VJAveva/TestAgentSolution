using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TestController.Api.Security;

/// <summary>
/// Authentication mode provider for "None" mode.
/// Treats all requests as Admin — use only in development or trusted single-machine deployments.
/// </summary>
public sealed class NoneAuthModeProvider : IAuthenticationModeProvider
{
    public AuthMode Mode => AuthMode.None;
    public bool IsHealthy => true;

    public UserRole ResolveRole(ClaimsPrincipal user) => UserRole.Admin;

    public string GetDiagnosticStatus() => "None mode: authentication disabled, all users treated as Admin";
}

/// <summary>
/// Authentication handler that always succeeds without requiring credentials.
/// Assigns a synthetic identity with Admin role to every request.
/// </summary>
public sealed class NoneAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public NoneAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Environment.MachineName),
            new Claim(ClaimTypes.Name, Environment.UserName),
            new Claim(ClaimTypes.Role, "Admin"),
        };
        var identity = new ClaimsIdentity(claims, "None");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "None");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
