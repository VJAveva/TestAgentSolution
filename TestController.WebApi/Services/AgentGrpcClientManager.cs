using System.Collections.Concurrent;
using Grpc.Net.Client;
using TestAgentGrpc;

namespace TestController.WebApi.Services;

/// <summary>
/// Manages gRPC channels to agent nodes. Similar to the WPF AgentGrpcDispatcher
/// but without UI event patterns — designed for use with SignalR broadcasting.
/// </summary>
public sealed class AgentGrpcClientManager : IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);

    public TestAgentService.TestAgentServiceClient GetClient(string address)
    {
        var channel = _channels.GetOrAdd(address, addr =>
            GrpcChannel.ForAddress(addr, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    ConnectTimeout               = TimeSpan.FromSeconds(30),
                    // Keep gRPC streams alive during long test runs (2-4+ hours).
                    // Pings every 60s prevent proxies/firewalls from killing
                    // idle-looking HTTP/2 streams when no stdout is flowing.
                    KeepAlivePingDelay            = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout          = TimeSpan.FromSeconds(30),
                    KeepAlivePingPolicy           = HttpKeepAlivePingPolicy.Always,
                    PooledConnectionIdleTimeout   = TimeSpan.FromMinutes(5),
                    // Do NOT recycle connections with a short lifetime.
                    PooledConnectionLifetime      = Timeout.InfiniteTimeSpan,
                }
            }));
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
