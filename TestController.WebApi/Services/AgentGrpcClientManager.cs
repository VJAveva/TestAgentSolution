using System.Collections.Concurrent;
using Grpc.Net.Client;
using TestAgentGrpc;
using TestController.Api.Security;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Services;

/// <summary>
/// Manages gRPC channels to agent nodes. Similar to the WPF AgentGrpcDispatcher
/// but without UI event patterns — designed for use with SignalR broadcasting.
/// </summary>
public sealed class AgentGrpcClientManager : IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly GrpcTlsChannelFactory? _tlsFactory;
    private readonly ControllerTimeoutOptions _timeouts;

    public AgentGrpcClientManager(GrpcTlsChannelFactory? tlsFactory = null, ControllerTimeoutOptions? timeouts = null)
    {
        _tlsFactory = tlsFactory;
        _timeouts = timeouts ?? new ControllerTimeoutOptions();
    }

    public TestAgentService.TestAgentServiceClient GetClient(string address)
    {
        var channel = _channels.GetOrAdd(address, addr =>
        {
            // Use TLS factory if available and TLS mode is active
            if (_tlsFactory != null && _tlsFactory.ShouldUseTls)
            {
                return _tlsFactory.CreateChannel(addr);
            }

            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                ConnectTimeout               = TimeSpan.FromSeconds(_timeouts.ChannelConnectTimeoutSeconds),
                KeepAlivePingDelay            = TimeSpan.FromSeconds(_timeouts.KeepAlivePingDelaySeconds),
                KeepAlivePingTimeout          = TimeSpan.FromSeconds(_timeouts.KeepAlivePingTimeoutSeconds),
                KeepAlivePingPolicy           = HttpKeepAlivePingPolicy.Always,
                PooledConnectionIdleTimeout   = TimeSpan.FromMinutes(_timeouts.PooledConnectionIdleMinutes),
                PooledConnectionLifetime      = Timeout.InfiniteTimeSpan,
            };
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                DefaultRequestVersion = new Version(2, 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            return GrpcChannel.ForAddress(addr, new GrpcChannelOptions
            {
                HttpClient = httpClient,
                DisposeHttpClient = true,
            });
        });
        return new TestAgentService.TestAgentServiceClient(channel);
    }

    public void RemoveChannel(string address)
    {
        if (_channels.TryRemove(address, out var channel))
            channel.Dispose();
    }

    public void Dispose()
    {
        foreach (var ch in _channels.Values)
            ch.Dispose();
        _channels.Clear();
    }
}
