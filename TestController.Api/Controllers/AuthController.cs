using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// REST endpoints for authentication.
/// GET /api/auth/me — current user info.
/// POST /api/auth/login — issue session token.
/// POST /api/auth/logout — revoke session.
/// POST /api/auth/change-password — change current user password.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly SessionAuthInterceptor _authInterceptor;
    private readonly IOptionsMonitor<RbacOptions> _rbacOptions;

    public AuthController(
        AuthService authService,
        SessionAuthInterceptor authInterceptor,
        IOptionsMonitor<RbacOptions> rbacOptions)
    {
        _authService = authService;
        _authInterceptor = authInterceptor;
        _rbacOptions = rbacOptions;
    }

    /// <summary>GET /api/auth/me — returns current user info for the session.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var clientKind = ClientKind.Web;

        var user = await _authInterceptor.ResolveUserAsync(authHeader, clientKind, ct);
        if (user is null)
            return Unauthorized(new { error = "Not authenticated" });

        // Build capabilities from role
        var capabilities = PermissionCatalog.GetPermissionsForRole(user.Roles.FirstOrDefault())
            .Select(p => p.ToString())
            .ToList();

        return Ok(new MeResponse(
            UserId: user.UserId,
            Username: user.DisplayName,
            DisplayName: user.DisplayName,
            Role: user.Roles.FirstOrDefault() ?? "Guest",
            ClientKind: user.ClientKind.ToString(),
            Capabilities: capabilities,
            MustChangePassword: false, // resolved post-login; /me is called after login
            IsGuest: user.GuestId is not null,
            AssignedPipelineIds: user.AssignedPipelineIds.ToList()
        ));
    }

    /// <summary>POST /api/auth/login — authenticate and issue session token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _authService.LoginAsync(request.Username, request.Password, ClientKind.Web, ipAddress, ct);

        if (!result.Success)
            return Unauthorized(new { error = result.Error });

        return Ok(new LoginResponse(
            Token: result.Token!,
            Role: result.Role?.ToString() ?? "Guest",
            MustChangePassword: result.MustChangePassword
        ));
    }

    /// <summary>POST /api/auth/guest — issue guest session token.</summary>
    [HttpPost("guest")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginAsGuest(CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _authService.LoginAsGuestAsync(ClientKind.Web, ipAddress, ct);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(new LoginResponse(
            Token: result.Token!,
            Role: "Guest",
            MustChangePassword: false
        ));
    }

    /// <summary>POST /api/auth/logout — revoke current session.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return Ok();

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : authHeader.Trim();

        await _authService.LogoutAsync(token, ct);
        return Ok();
    }

    /// <summary>POST /api/auth/change-password — change password (requires current password).</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var user = await _authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web, ct);
        if (user is null)
            return Unauthorized(new { error = "Not authenticated" });

        var (success, error) = await _authService.ChangePasswordAsync(
            user.UserId, request.CurrentPassword, request.NewPassword, ct);

        if (!success)
            return BadRequest(new { error });

        return Ok(new { message = "Password changed successfully" });
    }
}

public sealed record MeResponse(
    string UserId,
    string Username,
    string DisplayName,
    string Role,
    string ClientKind,
    List<string> Capabilities,
    bool MustChangePassword,
    bool IsGuest,
    List<string> AssignedPipelineIds);

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, string Role, bool MustChangePassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
