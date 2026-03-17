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
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
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
