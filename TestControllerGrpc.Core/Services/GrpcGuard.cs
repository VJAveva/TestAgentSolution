using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace TestControllerGrpc.Services;

/// <summary>
/// Wraps a gRPC handler body so the REAL server-side exception is always logged
/// (with the inbound correlation id) before a sanitized <see cref="RpcException"/>
/// is returned to the caller. Benign cancellations and already-structured
/// <see cref="RpcException"/>s pass through untouched.
/// </summary>
public static class GrpcGuard
{
    private static string? Corr(ServerCallContext ctx) =>
        ctx.RequestHeaders.GetValue("x-correlation-id");

    public static async Task<T> RunAsync<T>(
        IAppLogger log, string category, ServerCallContext ctx, Func<Task<T>> body)
    {
        try { return await body(); }
        catch (OperationCanceledException) { throw; }   // benign disconnect
        catch (RpcException) { throw; }                  // already structured
        catch (Exception ex)
        {
            log.Log(LogLevel.Error, category,
                $"Unhandled in {ctx.Method}: {ex.GetType().Name}: {ex.Message}",
                Corr(ctx), ex: ex);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public static async Task RunAsync(
        IAppLogger log, string category, ServerCallContext ctx, Func<Task> body)
    {
        try { await body(); }
        catch (OperationCanceledException) { throw; }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            log.Log(LogLevel.Error, category,
                $"Unhandled in {ctx.Method}: {ex.GetType().Name}: {ex.Message}",
                Corr(ctx), ex: ex);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
