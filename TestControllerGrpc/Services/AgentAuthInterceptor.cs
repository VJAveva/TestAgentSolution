using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace TestControllerGrpc.Services;

/// <summary>
/// Server interceptor that authenticates inbound agent RPCs (Register, UnRegister,
/// UpdateClientState, Heartbeat, PushExecutionEvents) against a configured shared
/// secret carried in the <c>x-agent-token</c> metadata header.
///
/// Fail-open when no secret is configured (warns once) so existing deployments keep
/// working; enforces <see cref="StatusCode.Unauthenticated"/> once a secret is set.
/// Uses a constant-time comparison so a mismatch cannot be probed via timing.
/// </summary>
public sealed class AgentAuthInterceptor : Interceptor
{
    private readonly byte[]? _secret;
    private readonly IAppLogger _logger;
    private static int _warnedDisabled;

    public AgentAuthInterceptor(AgentAuthOptions options, IAppLogger logger)
    {
        _logger = logger;
        _secret = string.IsNullOrEmpty(options.SharedSecret)
            ? null
            : Encoding.UTF8.GetBytes(options.SharedSecret);
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, context);
    }

    private void Authenticate(ServerCallContext context)
    {
        if (_secret is null)
        {
            if (Interlocked.Exchange(ref _warnedDisabled, 1) == 0)
                _logger.Warn("AgentAuth",
                    "Agent shared-secret NOT configured — agent RPCs are UNAUTHENTICATED. " +
                    "Set Controller:AgentSharedSecret (or AGENT_SHARED_SECRET) to enforce.");
            return;
        }

        var provided = context.RequestHeaders.GetValue(AgentAuthOptions.HeaderName);
        if (string.IsNullOrEmpty(provided) ||
            !CryptographicOperations.FixedTimeEquals(_secret, Encoding.UTF8.GetBytes(provided)))
        {
            _logger.Warn("AgentAuth",
                $"Rejected unauthenticated agent RPC {context.Method} from {context.Peer}");
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "Missing or invalid agent token"));
        }
    }
}
