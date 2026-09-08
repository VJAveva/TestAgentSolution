using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using TestController.Api.Controllers;

namespace TestController.WebApi.Services;

/// <summary>
/// Removes shared controllers that the standalone WebApi must NOT host directly because
/// it has no database. These operations are forwarded to the WPF controller via
/// <see cref="ControllerProxyService"/> through dedicated minimal-API proxy endpoints.
///
/// Without this, the shared controllers are registered alongside the proxy endpoints,
/// producing either an <c>AmbiguousMatchException</c> (e.g. GET /api/locks matching both
/// <see cref="LocksController"/> and the proxy) or a 500 from the secondary host's
/// Null* identity stubs (e.g. POST /api/auth/login hitting NullSessionStore).
/// </summary>
public sealed class ProxiedControllerExclusionProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private static readonly HashSet<Type> ExcludedControllers =
    [
        // Auth: requires the session store + user database — proxied via AuthEndpoints.
        typeof(AuthController),
        // Pipeline locks: require the lock registry/database — proxied via LockEndpoints.
        typeof(LocksController),
        // Notification mutes: MuteService is primary-host only — proxied via NotificationEndpoints.
        typeof(NotificationsController),
    ];

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers
                     .Where(c => ExcludedControllers.Contains(c.AsType()))
                     .ToList())
        {
            feature.Controllers.Remove(controller);
        }
    }
}
