using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace TestController.Api;

/// <summary>
/// Factory for consistent RFC 7807 problem+json error responses.
/// Used across all shared API controllers to prevent format divergence.
/// </summary>
public static class ApiErrorFactory
{
    public static ProblemDetails NotFound(string detail, string? instance = null) => new()
    {
        Status = StatusCodes.Status404NotFound,
        Title = "Resource not found",
        Detail = detail,
        Instance = instance,
    };

    public static ProblemDetails BadRequest(string detail, string? instance = null) => new()
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Invalid request",
        Detail = detail,
        Instance = instance,
    };

    public static ProblemDetails Conflict(string detail) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Resource conflict",
        Detail = detail,
    };

    public static ProblemDetails Forbidden(string detail) => new()
    {
        Status = StatusCodes.Status403Forbidden,
        Title = "Forbidden",
        Detail = detail,
    };

    public static ProblemDetails ServerError(string detail) => new()
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "Internal server error",
        Detail = detail,
    };

    /// <summary>
    /// Creates a ProblemDetails for invalid/nonexistent WatchList tag references.
    /// Used consistently across trigger, cancel, preflight, and query endpoints.
    /// </summary>
    public static ProblemDetails InvalidTag(string tag)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "WatchItem not found",
            Detail = $"WatchItem '{tag}' does not exist in the current WatchList configuration.",
        };
        problem.Extensions["tag"] = tag;
        return problem;
    }

    /// <summary>
    /// Creates a ProblemDetails for operations blocked by agent locks.
    /// </summary>
    public static ProblemDetails AgentsBusy(string detail, IEnumerable<object>? conflicts = null)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Agents are busy",
            Detail = detail,
        };
        if (conflicts is not null)
            problem.Extensions["conflicts"] = conflicts;
        return problem;
    }
}
