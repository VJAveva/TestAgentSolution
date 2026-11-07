using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Features;

namespace TestController.Api.Security;

/// <summary>
/// Returns 404 "feature disabled on this controller" when <see cref="Feature"/> is off. Evaluated per
/// request, so a Features.json change applies without a restart. No registered gate means on.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute(string feature) : Attribute, IAsyncResourceFilter
{
    public string Feature { get; } = feature;

    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (FeatureGuard.Disabled(context.HttpContext, Feature) is { } problem)
        {
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status404NotFound };
            return Task.CompletedTask;
        }
        return next();
    }
}

public static class FeatureGuard
{
    public const string DisabledTitle = "Feature disabled";

    /// <summary>Problem to return when the feature is off; null when the request may proceed.</summary>
    public static ProblemDetails? Disabled(HttpContext http, string feature)
    {
        var gate = http.RequestServices.GetService<IFeatureGate>();
        if (gate is null || gate.IsEnabled(feature)) return null;

        var state = gate.All().FirstOrDefault(s => string.Equals(s.Key, feature, StringComparison.OrdinalIgnoreCase));
        return new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = DisabledTitle,
            Detail = state?.OffBecause is { } why
                ? $"Feature '{feature}' is disabled on this controller ({why})."
                : $"Feature '{feature}' is disabled on this controller.",
            Instance = http.Request.Path,
        };
    }

    /// <summary>Minimal-API equivalent of <see cref="RequireFeatureAttribute"/>.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
            Disabled(context.HttpContext, feature) is { } problem
                ? Results.Problem(problem)
                : await next(context));
        return builder;
    }
}
