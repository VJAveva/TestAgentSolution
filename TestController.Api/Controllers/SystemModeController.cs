using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TestController.Api.Hubs;
using TestController.Api.SystemMode;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// REST endpoints for system mode (Default vs Secured).
/// Web Client uses GET for polling on reconnect; WPF uses gRPC but can also call these.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemModeController : ControllerBase
{
    private readonly IOptionsMonitor<RbacOptions> _rbacOptions;
    private readonly RbacModeTransitionService _transitionService;
    private readonly SystemModeBroadcaster _broadcaster;

    public SystemModeController(
        IOptionsMonitor<RbacOptions> rbacOptions,
        RbacModeTransitionService transitionService,
        SystemModeBroadcaster broadcaster)
    {
        _rbacOptions = rbacOptions;
        _transitionService = transitionService;
        _broadcaster = broadcaster;
    }

    /// <summary>GET /api/system/mode — current RBAC mode.</summary>
    [HttpGet("mode")]
    [AllowAnonymous]
    public IActionResult GetMode()
    {
        var enabled = _rbacOptions.CurrentValue.Enabled;
        return Ok(new
        {
            mode = enabled ? "secured" : "default",
            enabled,
        });
    }

    /// <summary>POST /api/system/mode/secured — switch to Secured mode (creates initial admin).</summary>
    [HttpPost("mode/secured")]
    public async Task<IActionResult> SwitchToSecured([FromBody] SwitchToSecuredRequest request, CancellationToken ct)
    {
        var (success, error) = await _transitionService.SwitchToSecuredAsync(
            request.Username, request.Email, request.Password, ct);

        if (!success)
            return BadRequest(new { error });

        await _broadcaster.BroadcastModeChangedAsync("secured");
        return Ok(new { mode = "secured" });
    }

    /// <summary>POST /api/system/mode/default — switch to Default mode (requires admin).</summary>
    [HttpPost("mode/default")]
    public async Task<IActionResult> SwitchToDefault(CancellationToken ct)
    {
        // In a full impl, resolve the current user from auth context.
        // For Phase 0.5 the WPF client is the only caller and it passes through the gRPC path.
        var actor = DefaultUser.ForClient(ClientKind.Wpf);

        var (success, error) = await _transitionService.SwitchToDefaultAsync(actor, ct);

        if (!success)
            return BadRequest(new { error });

        await _broadcaster.BroadcastModeChangedAsync("default");
        return Ok(new { mode = "default" });
    }
}

public sealed record SwitchToSecuredRequest(string Username, string Email, string Password);
