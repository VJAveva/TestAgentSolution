using Microsoft.AspNetCore.Mvc;
using TestController.Api.Contracts;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// REST endpoints for pipeline lock operations. Hosted by the WPF controller's
/// embedded Kestrel. The standalone WebApi proxies to these via ControllerProxyService.
/// Per phase-3a-context.md.
/// </summary>
[ApiController]
[Route("api/locks")]
public class LocksController : ControllerBase
{
    private readonly LockService? _lockService;
    private readonly SessionAuthInterceptor _authInterceptor;

    public LocksController(SessionAuthInterceptor authInterceptor, LockService? lockService = null)
    {
        _authInterceptor = authInterceptor;
        _lockService = lockService;
    }

    private async Task<IUserContext?> ResolveUser(CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        return await _authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web, ct);
    }

    /// <summary>GET /api/locks — list all active pipeline locks.</summary>
    [HttpGet]
    public IActionResult List()
    {
        if (_lockService is null)
            return Ok(Array.Empty<PipelineLockDto>());

        return Ok(_lockService.ListLocks());
    }

    /// <summary>GET /api/locks/{pipelineId} — get lock for a specific pipeline.</summary>
    [HttpGet("{pipelineId}")]
    public IActionResult Get(string pipelineId)
    {
        if (_lockService is null)
            return NotFound();

        var dto = _lockService.GetLock(pipelineId);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>DELETE /api/locks/{pipelineId} — release a lock (owner only).</summary>
    [HttpDelete("{pipelineId}")]
    public async Task<IActionResult> Release(string pipelineId, CancellationToken ct)
    {
        if (_lockService is null)
            return NotFound();

        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        var released = _lockService.ReleaseLock(pipelineId, user);
        return released
            ? Ok(new { message = "Lock released" })
            : Conflict(new { error = "Cannot release — not the lock owner or no lock exists" });
    }

    /// <summary>POST /api/locks/{pipelineId}/force-release — force-release with reason.</summary>
    [HttpPost("{pipelineId}/force-release")]
    public async Task<IActionResult> ForceRelease(string pipelineId, [FromBody] ForceReleaseRequest request, CancellationToken ct)
    {
        if (_lockService is null)
            return NotFound();

        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        var (success, conflictLock, error) = await _lockService.ForceReleaseAsync(
            pipelineId, request.Reason, user, ct);

        if (!success)
            return StatusCode(403, new { error });

        return Ok(new { message = "Lock force-released" });
    }
}

public sealed record ForceReleaseRequest(string Reason);
