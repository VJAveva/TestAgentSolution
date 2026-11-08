using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Sends Pipeline Builder calls to the WPF controller, which owns the Parameters folder, the WatchList and
/// the fragments folder. The controller re-checks the feature gate and Pipeline_Author with the same bearer
/// token. Without a configured controller the request falls through and the local controller answers 404.
/// </summary>
public sealed class PipelineBuilderForwardingMiddleware
{
    internal const string Prefix = "/api/pipeline-builder/";

    private readonly RequestDelegate _next;

    public PipelineBuilderForwardingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ControllerProxyService proxy, IAppLogger logger)
    {
        if (!proxy.IsConfigured || !ShouldForward(context.Request))
        {
            await _next(context);
            return;
        }

        var pathAndQuery = context.Request.Path + context.Request.QueryString;
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        HttpResponseMessage? response;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            response = await proxy.ForwardGetAsync(pathAndQuery, authHeader);
        }
        else
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            response = await proxy.ForwardWithBodyAsync(
                HttpMethod.Post, pathAndQuery, authHeader, body, context.Request.ContentType ?? "application/json");
        }

        using (response)
        {
            if (response is null)
            {
                logger.Warn("PipelineBuilderForward", $"Controller unreachable forwarding {context.Request.Method} {pathAndQuery}.");
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"error\":\"controller-unreachable\",\"detail\":\"The Pipeline Builder runs on the controller, which is not reachable. No files were written.\"}");
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType?.ToString() is { Length: > 0 } contentType)
                context.Response.ContentType = contentType;

            var payload = await response.Content.ReadAsByteArrayAsync();
            if (payload.Length > 0)
                await context.Response.Body.WriteAsync(payload);
        }
    }

    internal static bool ShouldForward(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsPost(request.Method))
        && (request.Path.Value ?? "").StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
