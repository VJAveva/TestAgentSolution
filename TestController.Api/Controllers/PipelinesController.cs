using Microsoft.AspNetCore.Mvc;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// REST endpoints for authorized pipeline trigger and cancel.
/// Per 02_Implementation_Roadmap.md §Phase 2 + phase-2a-context.md.
/// </summary>
[ApiController]
[Route("api/pipelines")]
public class PipelinesController : ControllerBase
{
    private readonly PipelineService _pipelineService;
    private readonly RetryService _retryService;
    private readonly EnableDisableService _enableDisableService;
    private readonly SessionAuthInterceptor _authInterceptor;

    public PipelinesController(
        PipelineService pipelineService,
        RetryService retryService,
        EnableDisableService enableDisableService,
        SessionAuthInterceptor authInterceptor)
    {
        _pipelineService = pipelineService;
        _retryService = retryService;
        _enableDisableService = enableDisableService;
        _authInterceptor = authInterceptor;
    }

    private async Task<IUserContext?> ResolveUser(CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        return await _authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web, ct);
    }

    /// <summary>POST /api/pipelines/{pipelineId}/trigger — authorize trigger.</summary>
    [HttpPost("{pipelineId}/trigger")]
    public async Task<IActionResult> Trigger(string pipelineId, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        try
        {
            var conflictDto = await _pipelineService.AuthorizeTriggerAsync(user, pipelineId, ct);
            if (conflictDto is not null)
                return Conflict(new { error = "pipeline-locked", @lock = conflictDto });

            return Ok(new { message = $"Trigger authorized for pipeline '{pipelineId}'." });
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            return StatusCode(403, new { error = ex.Message, reasonCode = ex.ReasonCode });
        }
    }

    /// <summary>POST /api/pipelines/{pipelineId}/cancel — authorize cancel.</summary>
    [HttpPost("{pipelineId}/cancel")]
    public async Task<IActionResult> Cancel(string pipelineId, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        try
        {
            await _pipelineService.AuthorizeCancelAsync(user, pipelineId, ct);
            return Ok(new { message = $"Cancel authorized for pipeline '{pipelineId}'." });
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            return StatusCode(403, new { error = ex.Message, reasonCode = ex.ReasonCode });
        }
    }

    /// <summary>POST /api/pipelines/{pipelineId}/retry — authorize retry of failed actions.</summary>
    [HttpPost("{pipelineId}/retry")]
    public async Task<IActionResult> Retry(string pipelineId, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        try
        {
            var conflictDto = await _retryService.AuthorizeRetryAsync(user, pipelineId, ct);
            if (conflictDto is not null)
                return Conflict(new { error = "pipeline-locked", @lock = conflictDto });

            return Ok(new { message = $"Retry authorized for pipeline '{pipelineId}'." });
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            return StatusCode(403, new { error = ex.Message, reasonCode = ex.ReasonCode });
        }
    }

    /// <summary>POST /api/pipelines/{pipelineId}/enabled — set pipeline enabled/disabled state.</summary>
    [HttpPost("{pipelineId}/enabled")]
    public async Task<IActionResult> SetEnabled(string pipelineId, [FromBody] SetEnabledRequest request, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        try
        {
            await _enableDisableService.SetEnabledAsync(user, pipelineId, request.Enabled, ct);
            return Ok(new { message = $"Pipeline '{pipelineId}' is now {(request.Enabled ? "enabled" : "disabled")}." });
        }
        catch (PipelineAuthorizationDeniedException ex)
        {
            return StatusCode(403, new { error = ex.Message, reasonCode = ex.ReasonCode });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    public sealed record SetEnabledRequest(bool Enabled);
}
