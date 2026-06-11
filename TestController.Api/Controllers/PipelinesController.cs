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
    private readonly SessionAuthInterceptor _authInterceptor;

    public PipelinesController(PipelineService pipelineService, SessionAuthInterceptor authInterceptor)
    {
        _pipelineService = pipelineService;
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
            await _pipelineService.AuthorizeTriggerAsync(user, pipelineId, ct);
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
}
