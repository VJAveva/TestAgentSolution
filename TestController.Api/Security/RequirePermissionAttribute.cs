using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TestControllerGrpc.Authorization;

namespace TestController.Api.Security;

/// <summary>
/// Requires a fine-grained <see cref="Permission"/> for an action, on top of whatever
/// <c>[Authorize]</c> policy the controller already carries.
/// </summary>
/// <remarks>
/// Applied per action rather than at controller level: authorization is one of the few places where being
/// explicit beats being terse, and a class-level default would silently gate endpoints that must stay open
/// (the health probe). No-ops in Default mode via <see cref="RbacGate"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly Permission _permission;

    public RequirePermissionAttribute(Permission permission) => _permission = permission;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool allowed = await RbacGate.IsAuthorizedAsync(
            context.HttpContext, _permission, resourceId: null, context.HttpContext.RequestAborted);

        if (allowed) return;

        ProblemDetails problem = ApiErrorFactory.Forbidden(
            $"Requires the {_permission} permission (Administrator or Senior Manager).");
        // The client needs the permission name to render a useful message rather than a generic error.
        problem.Extensions["code"] = "PERMISSION_DENIED";
        problem.Extensions["requiredPermission"] = _permission.ToString();

        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
