using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Api.Security;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// Pipeline Builder over REST: the same <see cref="PipelineBuilderService"/> the WPF panel uses, so the
/// web and desktop produce identical files. Served by the controller app only; the standalone WebApi
/// forwards here. Create writes a config + WatchList fragment; it never edits the live WatchList or runs anything.
/// </summary>
[ApiController]
[Route("api/pipeline-builder")]
[Authorize(Policy = SecurityPolicies.User)]
[RequireFeature(FeatureKeys.PipelineBuilder)]
[RequirePermission(Permission.Pipeline_Author)]
public partial class PipelineBuilderController : ControllerBase
{
    private const int MaxAgents = 64;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex AgentName();

    public sealed record BuilderSelection(string? TargetId, string? RecipeName, List<string>? AgentNames);

    /// <summary>GET /api/pipeline-builder/catalog — targets, recipes and agents to choose from.</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog()
    {
        if (Host() is not { } host) return NotHosted();

        var targets = host.Builder.Targets();
        var recipes = host.Builder.Recipes();
        return Ok(new
        {
            targets = targets.Targets.Select(t => new { id = t.Id, releaseName = t.ReleaseName, profiles = t.Profiles }),
            recipes = recipes.Recipes.Select(r => new
            {
                name = r.Name,
                stages = r.Stages.Select(s => new { name = s.Name, templateId = s.TemplateId, agentMapping = s.AgentMapping.ToString() }),
            }),
            agents = host.Agents.GetAgents().Select(a => new { name = a.Name, status = a.Status, isOnline = a.IsOnline, isBusy = a.IsBusy }),
            problems = targets.Problems.Concat(recipes.Problems),
        });
    }

    /// <summary>POST /api/pipeline-builder/preview — the generated config and WatchItem, secrets masked.</summary>
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] BuilderSelection? body)
    {
        if (Host() is not { } host) return NotHosted();
        if (Parse(body) is not { } request) return InvalidSelection();

        var preview = host.Builder.Preview(request);
        return Ok(new
        {
            tag = preview.Tag,
            title = preview.Title,
            configFileName = preview.ConfigFileName,
            triggerFolder = preview.TriggerFolder,
            triggerFilter = preview.TriggerFilter,
            stageTemplatesUsed = preview.StageTemplatesUsed,
            configJson = preview.ConfigJson.Length == 0 ? "" : BuilderRedaction.ConfigJson(preview.Config),
            watchItemXml = preview.WatchItem is null ? "" : BuilderRedaction.WatchItemXml(preview.WatchItem),
            hasErrors = preview.HasErrors,
            issues = Issues(preview.Issues),
        });
    }

    /// <summary>POST /api/pipeline-builder/validate — the authoring gate, same checks as the WPF Review step.</summary>
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] BuilderSelection? body)
    {
        if (Host() is not { } host) return NotHosted();
        if (Parse(body) is not { } request) return InvalidSelection();

        var preview = host.Builder.Preview(request);
        var validation = host.Builder.Validate(preview, host.LiveConfig(), host.FragmentDir);
        return Ok(new
        {
            tag = preview.Tag,
            canCreate = validation.CanCreate,
            blocking = Issues(validation.Blocking),
            informational = Issues(validation.Informational),
            tokens = validation.Tokens.Select(t => new
            {
                name = t.Name,
                value = BuilderRedaction.Value(t.Name, t.DisplayValue),
                source = t.Source,
                status = t.Status.ToString(),
                usedBy = t.UsedBy,
            }),
        });
    }

    /// <summary>
    /// POST /api/pipeline-builder/create — rebuilds the preview from the selection on the server (never
    /// trusts a client-supplied config) and writes the config + fragment. Nothing is imported or run.
    /// </summary>
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] BuilderSelection? body, CancellationToken ct)
    {
        if (Host() is not { } host) return NotHosted();
        if (Parse(body) is not { } request) return InvalidSelection();

        var isWpf = string.Equals(Request.Headers["X-Source"].FirstOrDefault(), "WPF", StringComparison.OrdinalIgnoreCase);
        var rbacOn = HttpContext.RequestServices.GetService<IOptionsMonitor<RbacOptions>>()?.CurrentValue.Enabled == true;
        // RequirePermission fails open in Default mode, where the web client is read-only by design.
        if (!rbacOn && !isWpf)
            return StatusCode(StatusCodes.Status403Forbidden, ApiErrorFactory.Forbidden(
                "Pipelines can only be created by an Administrator in Secured mode, or from the controller app."));

        var kind = isWpf ? ClientKind.Wpf : ClientKind.Web;
        var user = HttpContext.RequestServices.GetService<SessionAuthInterceptor>() is { } interceptor
            ? await interceptor.ResolveUserAsync(Request.Headers.Authorization.FirstOrDefault(), kind, ct)
            : null;
        if (user is null)
        {
            if (rbacOn)
                return StatusCode(StatusCodes.Status403Forbidden, ApiErrorFactory.Forbidden("Could not identify the caller."));
            user = DefaultUser.ForClient(kind);
        }

        var preview = host.Builder.Preview(request);
        var live = host.LiveConfig();
        var validation = host.Builder.Validate(preview, live, host.FragmentDir);
        var outcome = await host.Builder.CreateAsync(preview, validation, host.FragmentDir, user, ct, live);

        var result = new
        {
            written = outcome.Written,
            tag = preview.Tag,
            configPath = outcome.ConfigPath,
            fragmentPath = outcome.FragmentPath,
            triggerFolder = outcome.TriggerFolder,
            issues = Issues(outcome.Issues),
        };
        return outcome.Written ? Ok(result) : UnprocessableEntity(result);
    }

    private PipelineBuilderHost? Host() => HttpContext.RequestServices.GetService<PipelineBuilderHost>();

    private NotFoundObjectResult NotHosted() => NotFound(ApiErrorFactory.NotFound(
        "The Pipeline Builder is served by the controller app. This host has no Parameters folder or WatchList to build from."));

    private BadRequestObjectResult InvalidSelection() => BadRequest(ApiErrorFactory.BadRequest(
        $"Body must be {{ \"targetId\", \"recipeName\", \"agentNames\": [...] }} with 1-{MaxAgents} agent names of [A-Za-z0-9._-]."));

    private static BuilderRequest? Parse(BuilderSelection? body)
    {
        if (body is null || !IsName(body.TargetId) || !IsName(body.RecipeName)) return null;
        if (body.AgentNames is not { Count: > 0 and <= MaxAgents } agents || !agents.All(a => a is not null && AgentName().IsMatch(a)))
            return null;
        return new BuilderRequest(body.TargetId!.Trim(), body.RecipeName!.Trim(), agents);
    }

    private static bool IsName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0
        && !value.Any(char.IsControl);

    private static IEnumerable<object> Issues(IEnumerable<BuilderIssue> issues) =>
        issues.Select(i => new { severity = i.Severity.ToString(), message = i.Message });
}
