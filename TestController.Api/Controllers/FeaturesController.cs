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
            codeChurn = new
            {
                configured = gate?.CodeChurnRepositories is not null,
                // CredentialRef is an environment variable NAME; the value never leaves the controller.
                repositories = (gate?.CodeChurnRepositories ?? []).Select(r => new
                {
                    name = r.Name,
                    url = r.Url,
                    branches = r.Branches,
                    includePaths = r.IncludePaths,
                    excludePaths = r.ExcludePaths,
                    credentialRef = r.CredentialRef,
                }),
            },
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
        if (ForbiddenInDefaultMode(isWpf) is { } forbidden) return forbidden;

        var settings = HttpContext.RequestServices.GetService<IFeatureSettingsService>();
        if (settings is null)
            return NotFound(ApiErrorFactory.NotFound("Feature settings are not available on this host."));

        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var user = await ResolveUserAsync(authHeader, isWpf, ct);

        var result = await settings.SaveAsync(desired, user, authHeader, ct);
        return Saved(result);
    }

    public sealed record CodeChurnRepositoryDto(
        string? Name, string? Url, List<string>? Branches, List<string>? IncludePaths, List<string>? ExcludePaths, string? CredentialRef);

    public sealed record CodeChurnUpdateRequest(List<CodeChurnRepositoryDto>? Repositories);

    /// <summary>PUT /api/features/code-churn — replace the Code Churn repository list. Administrator only.</summary>
    [HttpPut("code-churn")]
    [RequirePermission(Permission.System_ConfigureFeatures)]
    public async Task<IActionResult> PutCodeChurn([FromBody] CodeChurnUpdateRequest? body, CancellationToken ct)
    {
        var isWpf = string.Equals(Request.Headers["X-Source"].FirstOrDefault(), "WPF", StringComparison.OrdinalIgnoreCase);
        if (ForbiddenInDefaultMode(isWpf) is { } forbidden) return forbidden;

        if (body?.Repositories is null)
            return BadRequest(ApiErrorFactory.BadRequest("Body must be { \"repositories\": [ { \"name\": ..., \"branches\": [...] } ] }."));

        var repositories = body.Repositories.Select(r => new CodeChurnRepository(
            r.Name?.Trim() ?? "",
            string.IsNullOrWhiteSpace(r.Url) ? null : r.Url.Trim(),
            Clean(r.Branches), Clean(r.IncludePaths), Clean(r.ExcludePaths),
            string.IsNullOrWhiteSpace(r.CredentialRef) ? null : r.CredentialRef.Trim())).ToList();
        if (CodeChurnRepositoryRules.Validate(repositories) is { } invalid)
            return BadRequest(ApiErrorFactory.BadRequest(invalid));

        var settings = HttpContext.RequestServices.GetService<IFeatureSettingsService>();
        if (settings is null)
            return NotFound(ApiErrorFactory.NotFound("Feature settings are not available on this host."));

        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var user = await ResolveUserAsync(authHeader, isWpf, ct);
        return Saved(await settings.SaveCodeChurnAsync(repositories, user, authHeader, ct));

        static List<string> Clean(List<string>? values) =>
            (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).ToList();
    }

    // RequirePermission fails open in Default mode, where the web client is read-only by design.
    private IActionResult? ForbiddenInDefaultMode(bool isWpf)
    {
        var rbacOn = HttpContext.RequestServices.GetService<IOptionsMonitor<RbacOptions>>()?.CurrentValue.Enabled == true;
        return !rbacOn && !isWpf
            ? StatusCode(StatusCodes.Status403Forbidden, ApiErrorFactory.Forbidden(
                "Features can only be changed by an Administrator in Secured mode, or from the controller app."))
            : null;
    }

    private async Task<IUserContext?> ResolveUserAsync(string? authHeader, bool isWpf, CancellationToken ct) =>
        HttpContext.RequestServices.GetService<SessionAuthInterceptor>() is { } interceptor
            ? await interceptor.ResolveUserAsync(authHeader, isWpf ? ClientKind.Wpf : ClientKind.Web, ct)
            : null;

    private IActionResult Saved(FeatureSaveResult result) =>
        result.Success
            ? Ok(new { saved = true, message = result.Message, changes = result.Changes, savedAt = result.SavedAt })
            : StatusCode(StatusCodes.Status500InternalServerError, ApiErrorFactory.ServerError(result.Message));
}
