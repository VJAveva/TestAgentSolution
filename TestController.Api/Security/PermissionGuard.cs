using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;

namespace TestController.Api.Security;

/// <summary>Minimal-API equivalent of <see cref="RequirePermissionAttribute"/>.</summary>
public static class PermissionGuard
{
    /// <summary>
    /// Requires <paramref name="permission"/>. With <paramref name="denyInDefaultMode"/> the endpoint is refused
    /// for every caller while RBAC is off, instead of failing open: Default mode has no identities, so an
    /// endpoint that rewrites shared state would otherwise be open to anyone who can reach the host.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission, bool denyInDefaultMode = false)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var rbacOn = http.RequestServices.GetService<IOptionsMonitor<RbacOptions>>()?.CurrentValue.Enabled == true;

            if (!rbacOn && denyInDefaultMode)
                return Denied(permission, "Not available in Default mode. Switch to Secured mode and sign in as an Administrator.");

            if (!await RbacGate.IsAuthorizedAsync(http, permission, resourceId: null, http.RequestAborted))
                return Denied(permission, $"Requires the {permission} permission (Administrator).");

            return await next(context);
        });
        return builder;
    }

    private static IResult Denied(Permission permission, string detail)
    {
        var problem = ApiErrorFactory.Forbidden(detail);
        problem.Extensions["code"] = "PERMISSION_DENIED";
        problem.Extensions["requiredPermission"] = permission.ToString();
        return Results.Problem(problem);
    }
}
