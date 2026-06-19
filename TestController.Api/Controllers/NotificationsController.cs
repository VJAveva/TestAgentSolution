using Microsoft.AspNetCore.Mvc;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// REST endpoints for notification mute management.
/// Per Phase 8 — gated by Notification_Mute permission.
/// </summary>
[ApiController]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly MuteService _muteService;
    private readonly SessionAuthInterceptor _authInterceptor;

    public NotificationsController(MuteService muteService, SessionAuthInterceptor authInterceptor)
    {
        _muteService = muteService;
        _authInterceptor = authInterceptor;
    }

    /// <summary>GET /api/notifications/mutes — list all active mutes.</summary>
    [HttpGet("mutes")]
    public async Task<IActionResult> ListMutes(CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        var mutes = await _muteService.ListMutesAsync(ct);
        return Ok(mutes.Select(m => new MuteDto
        {
            MuteId = m.MuteId,
            Target = m.Target,
            TargetType = m.TargetType,
            MutedByUserId = m.MutedByUserId,
            MutedAtUtc = m.MutedAtUtc,
            ExpiresAtUtc = m.ExpiresAtUtc,
        }));
    }

    /// <summary>POST /api/notifications/mutes — mute a pipeline or test.</summary>
    [HttpPost("mutes")]
    public async Task<IActionResult> Mute([FromBody] MuteRequest request, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        if (string.IsNullOrWhiteSpace(request.Target))
            return BadRequest(new { error = "Target is required" });

        var targetType = request.TargetType ?? "Pipeline";
        if (targetType is not "Pipeline" and not "Test")
            return BadRequest(new { error = "TargetType must be 'Pipeline' or 'Test'" });

        var (success, error) = await _muteService.MuteAsync(user, request.Target, targetType, ct);
        if (!success)
            return StatusCode(403, new { error });

        return Ok(new { muted = true });
    }

    /// <summary>DELETE /api/notifications/mutes/{id} — unmute.</summary>
    [HttpDelete("mutes/{id:long}")]
    public async Task<IActionResult> Unmute(long id, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });

        var (success, error) = await _muteService.UnmuteAsync(user, id, ct);
        if (!success)
        {
            if (error == "Mute not found") return NotFound(new { error });
            return StatusCode(403, new { error });
        }

        return Ok(new { unmuted = true });
    }

    private async Task<IUserContext?> ResolveUser(CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        return await _authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web, ct);
    }
}

public sealed class MuteRequest
{
    public string Target { get; set; } = "";
    public string? TargetType { get; set; }
}

public sealed class MuteDto
{
    public long MuteId { get; set; }
    public string Target { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string MutedByUserId { get; set; } = "";
    public DateTime MutedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
