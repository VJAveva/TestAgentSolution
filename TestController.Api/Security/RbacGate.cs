using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Api.Security;

/// <summary>
/// Fine-grained RBAC gate for REST actions (P4-1 pattern).
/// </summary>
/// <remarks>
/// Fails OPEN when RBAC is disabled (Default mode) or its services are not wired, so behaviour is unchanged
/// until Secured mode is switched on. Services are resolved lazily from the request because the standalone
/// WebApi host does not register the RBAC stack at all.
///
/// Shared deliberately: three controllers enforce permissions this way, and three copies of an authorization
/// decision is how the copies drift apart.
/// </remarks>
internal static class RbacGate
{
    internal static async Task<bool> IsAuthorizedAsync(
        HttpContext http, Permission permission, string? resourceId, CancellationToken ct)
    {
        var rbacOptions = http.RequestServices.GetService<IOptionsMonitor<RbacOptions>>();
        if (rbacOptions is null || !rbacOptions.CurrentValue.Enabled)
            return true;

        var authzService = http.RequestServices.GetService<IAuthorizationService>();
        var authInterceptor = http.RequestServices.GetService<SessionAuthInterceptor>();
        if (authzService is null || authInterceptor is null)
            return true;

        var source = http.Request.Headers["X-Source"].FirstOrDefault();
        var clientKind = string.Equals(source, "WPF", StringComparison.OrdinalIgnoreCase)
            ? ClientKind.Wpf : ClientKind.Web;

        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        IUserContext? user = await authInterceptor.ResolveUserAsync(authHeader, clientKind, ct);
        if (user is null)
            return false;

        AuthDecision decision = await authzService.CanAsync(user, permission, resourceId, ct);
        return decision.Allowed;
    }
}
