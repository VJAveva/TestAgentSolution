using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TestController.Api.Hubs;
using TestController.Api.Interceptors;
using TestController.Api.SystemMode;
using TestControllerGrpc.Authorization;
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
    private readonly SessionAuthInterceptor _authInterceptor;
    private readonly TestControllerGrpc.Authorization.IAuthorizationService _authService;

    public SystemModeController(
        IOptionsMonitor<RbacOptions> rbacOptions,
        RbacModeTransitionService transitionService,
        SystemModeBroadcaster broadcaster,
        SessionAuthInterceptor authInterceptor,
        TestControllerGrpc.Authorization.IAuthorizationService authService)
    {
        _rbacOptions = rbacOptions;
        _transitionService = transitionService;
        _broadcaster = broadcaster;
        _authInterceptor = authInterceptor;
        _authService = authService;
    }

    /// <summary>
    /// P4-1 conditional-admin gate for mode transitions. Resolves the caller via the
    /// session interceptor (Default mode yields the synthetic WPF/Web user) and requires
    /// the System_ChangeMode permission. In Default mode this admits the trusted WPF host
    /// and denies the read-only Web client; in Secured mode it requires an authenticated
    /// Administrator. Returns the authorized actor, or null when unauthenticated/denied.
    /// </summary>
    private async Task<IUserContext?> AuthorizeModeChangeAsync(CancellationToken ct)
    {
        var source = Request.Headers["X-Source"].FirstOrDefault();
        var clientKind = string.Equals(source, "WPF", StringComparison.OrdinalIgnoreCase)
            ? ClientKind.Wpf : ClientKind.Web;
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var user = await _authInterceptor.ResolveUserAsync(authHeader, clientKind, ct);
        if (user is null) return null;
        var decision = await _authService.CanAsync(user, Permission.System_ChangeMode, null, ct);
        return decision.Allowed ? user : null;
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
        try
        {
            // Bootstrap is open only while no Administrator exists yet (first-run wizard).
            // Once an admin exists, re-securing requires System_ChangeMode (conditional admin gate).
            if (await _transitionService.HasExistingAdminAsync(ct)
                && await AuthorizeModeChangeAsync(ct) is null)
            {
                return StatusCode(403, ApiErrorFactory.Forbidden("Not authorized to change system mode."));
            }

            var (success, error) = await _transitionService.SwitchToSecuredAsync(
                request.Username, request.Email, request.Password, ct);

            if (!success)
                return BadRequest(new { error });

            await _broadcaster.BroadcastModeChangedAsync("secured");
            return Ok(new { mode = "secured" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiErrorFactory.ServerError(ex.Message));
        }
    }

    /// <summary>GET /api/system/mode/admin-exists — check if any Administrator account exists (active or archived).</summary>
    [HttpGet("mode/admin-exists")]
    [AllowAnonymous]
    public async Task<IActionResult> AdminExists(CancellationToken ct)
    {
        try
        {
            var exists = await _transitionService.HasExistingAdminAsync(ct);
            return Ok(new { exists });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiErrorFactory.ServerError(ex.Message));
        }
    }

    /// <summary>POST /api/system/mode/secured/reactivate — switch to Secured by reactivating existing users (no wizard).</summary>
    [HttpPost("mode/secured/reactivate")]
    public async Task<IActionResult> SwitchToSecuredReactivate(CancellationToken ct)
    {
        try
        {
            // Reactivation implies admins already exist → require System_ChangeMode.
            if (await AuthorizeModeChangeAsync(ct) is null)
                return StatusCode(403, ApiErrorFactory.Forbidden("Not authorized to change system mode."));

            var (success, error) = await _transitionService.SwitchToSecuredReactivateAsync(ct);

            if (!success)
                return BadRequest(new { error });

            await _broadcaster.BroadcastModeChangedAsync("secured");
            return Ok(new { mode = "secured" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiErrorFactory.ServerError(ex.Message));
        }
    }

    /// <summary>POST /api/system/mode/default — switch to Default mode (requires admin).</summary>
    [HttpPost("mode/default")]
    public async Task<IActionResult> SwitchToDefault(CancellationToken ct)
    {
        try
        {
            // Disabling RBAC requires System_ChangeMode (Administrator in Secured mode,
            // or the trusted WPF host in Default mode).
            var actor = await AuthorizeModeChangeAsync(ct);
            if (actor is null)
                return StatusCode(403, ApiErrorFactory.Forbidden("Not authorized to change system mode."));

            var (success, error) = await _transitionService.SwitchToDefaultAsync(actor, ct);

            if (!success)
                return BadRequest(new { error });

            await _broadcaster.BroadcastModeChangedAsync("default");
            return Ok(new { mode = "default" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiErrorFactory.ServerError(ex.Message));
        }
    }
}

public sealed record SwitchToSecuredRequest(string Username, string Email, string Password);
