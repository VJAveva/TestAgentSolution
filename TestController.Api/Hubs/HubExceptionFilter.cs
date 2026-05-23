using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace TestController.Api.Hubs;

/// <summary>
/// Global hub filter that catches unhandled exceptions in all hub methods,
/// logs them, and prevents connection drops from unexpected failures.
/// </summary>
public sealed class HubExceptionFilter : IHubFilter
{
    private readonly ILogger<HubExceptionFilter> _logger;

    public HubExceptionFilter(ILogger<HubExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[SignalR] Unhandled exception in hub method {Method} from connection {ConnectionId}",
                invocationContext.HubMethodName,
                invocationContext.Context.ConnectionId);

            throw new HubException($"Server error in {invocationContext.HubMethodName}. Please retry.");
        }
    }
}
