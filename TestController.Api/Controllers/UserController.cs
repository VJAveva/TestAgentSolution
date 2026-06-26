using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TestController.Api.Hubs;
using TestController.Api.Interceptors;
using TestController.Api.Services;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// REST endpoints for user management. All require User_* permissions (Admin only).
/// Per 02_Implementation_Roadmap.md §Phase 1.
/// </summary>
[ApiController]
[Route("api/users")]
public class UserController : ControllerBase
{
    private readonly UserService _userService;
    private readonly SessionAuthInterceptor _authInterceptor;
    private readonly IHubContext<ControllerHub> _hub;
    private readonly IOptionsMonitor<RbacOptions> _rbacOptions;
    private readonly IAuthorizationService _authService;

    public UserController(
        UserService userService,
        SessionAuthInterceptor authInterceptor,
        IHubContext<ControllerHub> hub,
        IOptionsMonitor<RbacOptions> rbacOptions,
        IAuthorizationService authService)
    {
        _userService = userService;
        _authInterceptor = authInterceptor;
        _hub = hub;
        _rbacOptions = rbacOptions;
        _authService = authService;
    }

    private async Task<IUserContext?> ResolveUser(CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        return await _authInterceptor.ResolveUserAsync(authHeader, ClientKind.Wpf, ct);
    }

    private bool HasPermission(IUserContext user, string permission)
    {
        // Admin-only: check role directly
        return user.Roles.Contains(Role.Administrator.ToString());
    }

    /// <summary>
    /// P4-1: fine-grained permission gate for mutating user-management actions. In Secured
    /// mode it delegates to <c>IAuthorizationService.CanAsync</c> (proper RBAC evaluation +
    /// audit entry); in Default mode it preserves the legacy Administrator-role gate.
    /// </summary>
    private async Task<bool> AuthorizeAsync(IUserContext user, Permission permission, CancellationToken ct)
    {
        if (!_rbacOptions.CurrentValue.Enabled)
            return user.Roles.Contains(Role.Administrator.ToString());
        var decision = await _authService.CanAsync(user, permission, null, ct);
        return decision.Allowed;
    }

    /// <summary>GET /api/users — list all users.</summary>
    [HttpGet]
    public async Task<IActionResult> ListUsers([FromQuery] string? username, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!HasPermission(user, "User_Create")) return Forbid();

        var users = await _userService.ListUsersAsync(username, ct);
        return Ok(users);
    }

    /// <summary>GET /api/users/{id} — get a single user.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(string id, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!HasPermission(user, "User_Create")) return Forbid();

        var result = await _userService.GetUserAsync(id, ct);
        if (result is null) return NotFound(new { error = "User not found" });
        return Ok(result);
    }

    /// <summary>POST /api/users — create a new user.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] UserService.CreateUserRequest request, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!await AuthorizeAsync(user, Permission.User_Create, ct)) return Forbid();

        var (dto, generatedPassword, error) = await _userService.CreateUserAsync(request, user.UserId, ct);
        if (dto is null)
        {
            if (error == "Username already exists")
                return Conflict(new { error });
            return BadRequest(new { error });
        }

        return Ok(new { user = dto, generatedPassword });
    }

    /// <summary>PUT /api/users/{id} — update user.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(string id, [FromBody] UserService.UpdateUserRequest request, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!await AuthorizeAsync(user, Permission.User_Update, ct)) return Forbid();

        var (success, error) = await _userService.UpdateUserAsync(id, request, user.UserId, ct);
        if (!success) return BadRequest(new { error });
        return Ok(new { message = "User updated" });
    }

    /// <summary>DELETE /api/users/{id} — delete user.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(string id, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!await AuthorizeAsync(user, Permission.User_Delete, ct)) return Forbid();

        var (success, error) = await _userService.DeleteUserAsync(id, user.UserId, ct);
        if (!success)
        {
            if (error == "Cannot delete the last Administrator")
                return Conflict(new { error });
            return BadRequest(new { error });
        }
        return Ok(new { message = "User deleted" });
    }

    /// <summary>POST /api/users/{id}/assign-pipelines — set pipeline assignments (bulk diff).</summary>
    [HttpPost("{id}/assign-pipelines")]
    public async Task<IActionResult> AssignPipelines(string id, [FromBody] UserService.AssignPipelinesRequest request, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!await AuthorizeAsync(user, Permission.User_Assign, ct)) return Forbid();

        var (success, error) = await _userService.SetAssignmentsAsync(id, request.PipelineIds, user.UserId, ct);
        if (!success) return BadRequest(new { error });

        // Fire-and-forget: signal all clients to refetch their own capabilities
        _ = _hub.Clients.All.SendAsync("PermissionsChanged", new { changedUserId = id }, CancellationToken.None);

        return Ok(new { message = "Assignments updated" });
    }

    /// <summary>GET /api/users/{id}/assignments — get current assignments.</summary>
    [HttpGet("{id}/assignments")]
    public async Task<IActionResult> GetAssignments(string id, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!HasPermission(user, "User_Assign")) return Forbid();

        var assignments = await _userService.GetAssignmentsAsync(id, ct);
        return Ok(new { pipelineIds = assignments });
    }

    /// <summary>POST /api/users/{id}/reset-password — admin-initiated password reset.</summary>
    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!await AuthorizeAsync(user, Permission.User_Update, ct)) return Forbid();

        var (newPassword, error) = await _userService.ResetPasswordAsync(id, user.UserId, ct);
        if (newPassword is null) return BadRequest(new { error });
        return Ok(new { newPassword });
    }

    /// <summary>GET /api/users/check-username?username=xxx — check username availability.</summary>
    [HttpGet("check-username")]
    public async Task<IActionResult> CheckUsername([FromQuery] string username, CancellationToken ct)
    {
        var user = await ResolveUser(ct);
        if (user is null) return Unauthorized(new { error = "Not authenticated" });
        if (!HasPermission(user, "User_Create")) return Forbid();

        var exists = await _userService.UsernameExistsAsync(username, ct);
        return Ok(new { exists });
    }
}
