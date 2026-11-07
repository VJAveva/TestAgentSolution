using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Api.Security;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

[ApiController]
[Route("api/features")]
[Authorize(Policy = SecurityPolicies.User)]
public class FeaturesController : ControllerBase
{
    /// <summary>GET /api/features — what this controller has turned on, for the clients to hide the rest.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var gate = HttpContext.RequestServices.GetService<IFeatureGate>();
        var features = gate?.All()
            ?? FeatureKeys.All.Select(k => new FeatureState(k, true, true, null,
                FeatureKeys.Descriptions.TryGetValue(k, out var d) ? d : "",
                FeatureKeys.DependsOn.TryGetValue(k, out var p) ? p : null)).ToList();

        return Ok(new
        {
            teamName = gate?.TeamName ?? "",
            features = features.Select(f => new
            {
                key = f.Key,
                enabled = f.Enabled,
                configuredOn = f.ConfiguredOn,
                offBecause = f.OffBecause,
                description = f.Description,
                dependsOn = f.DependsOn,
            }),
            map = features.ToDictionary(f => f.Key, f => f.Enabled),
            lastError = gate?.LastError,
        });
    }

    public sealed record FeatureUpdateRequest(Dictionary<string, bool>? Features);

    /// <summary>PUT /api/features — turn features on/off. Administrator only; the file is backed up first.</summary>
    [HttpPut]
    [RequirePermission(Permission.System_ConfigureFeatures)]
    public async Task<IActionResult> Put([FromBody] FeatureUpdateRequest? body, CancellationToken ct)
    {
        if (body?.Features is not { Count: > 0 } desired)
            return BadRequest(ApiErrorFactory.BadRequest("Body must be { \"features\": { \"<Key>\": true|false } }."));
        var unknown = desired.Keys.FirstOrDefault(k => !FeatureKeys.All.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            return BadRequest(ApiErrorFactory.BadRequest(
                $"'{unknown}' is not a toggleable feature. Valid keys: {string.Join(", ", FeatureKeys.All)}."));

        var source = Request.Headers["X-Source"].FirstOrDefault();
        var isWpf = string.Equals(source, "WPF", StringComparison.OrdinalIgnoreCase);
        var rbacOn = HttpContext.RequestServices.GetService<IOptionsMonitor<RbacOptions>>()?.CurrentValue.Enabled == true;
        // RequirePermission fails open in Default mode, where the web client is read-only by design.
        if (!rbacOn && !isWpf)
            return StatusCode(StatusCodes.Status403Forbidden, ApiErrorFactory.Forbidden(
                "Features can only be changed by an Administrator in Secured mode, or from the controller app."));

        var settings = HttpContext.RequestServices.GetService<IFeatureSettingsService>();
        if (settings is null)
            return NotFound(ApiErrorFactory.NotFound("Feature settings are not available on this host."));

        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        IUserContext? user = null;
        if (HttpContext.RequestServices.GetService<SessionAuthInterceptor>() is { } interceptor)
            user = await interceptor.ResolveUserAsync(authHeader, isWpf ? ClientKind.Wpf : ClientKind.Web, ct);

        var result = await settings.SaveAsync(desired, user, authHeader, ct);
        return result.Success
            ? Ok(new { saved = true, message = result.Message, changes = result.Changes, savedAt = result.SavedAt })
            : StatusCode(StatusCodes.Status500InternalServerError, ApiErrorFactory.ServerError(result.Message));
    }
}
