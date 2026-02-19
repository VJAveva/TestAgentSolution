using System.Collections.Concurrent;
using System.Net.Http;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using TestAgentGrpc;

namespace TestAgentDisplay.Services;

/// <summary>
/// Manages gRPC connections to multiple TestAgent nodes.
/// For each agent, opens a <c>SubscribeAgentEvents</c> streaming RPC
/// and forwards events to the UI via callbacks.
/// </summary>
public sealed class AgentConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new();

    public event Action<string, ExecutionEvent>? EventReceived;
    public event Action<string, bool>? ConnectionStateChanged;

    public bool IsConnected(string address) =>
        _connections.TryGetValue(address, out var c) && c.IsConnected;

    public async Task ConnectAsync(string address, CancellationToken ct = default)
    {
        if (_connections.ContainsKey(address)) return;

        var conn = new AgentConnection(address, this);
        _connections[address] = conn;
        ConnectionStateChanged?.Invoke(address, false);

        conn.Start(ct);
    }

    public void Disconnect(string address)
    {
        if (_connections.TryRemove(address, out var conn))
        {
            conn.Dispose();
            ConnectionStateChanged?.Invoke(address, false);
        }
    }

    public async Task<AgentSnapshot?> GetSnapshotAsync(string address, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(address, out var conn)) return null;
        try
        {
            return await conn.Client.GetAgentSnapshotAsync(new Empty(), cancellationToken: ct);
        }
        catch { return null; }
    }

    public async Task<ExecutionHistoryReply?> GetHistoryAsync(string address, int max = 50, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(address, out var conn)) return null;
        try
        {
            return await conn.Client.GetExecutionHistoryAsync(
                new ExecutionHistoryRequest { MaxResults = max }, cancellationToken: ct);
        }
        catch { return null; }
    }

    public async Task<RunCommandReply?> RunCommandAsync(string address, string command, string arguments, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(address, out var conn)) return null;
        try
        {
            return await conn.Client.RunCommandAsync(new RunCommandRequest
            {
                Command = command, Arguments = arguments
            }, cancellationToken: ct);
        }
        catch { return null; }
    }

    public async Task TerminateAsync(string address, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(address, out var conn)) return;
        try { await conn.Client.TerminateExecutionAsync(new Empty(), cancellationToken: ct); }
        catch { }
    }

    internal void OnEvent(string address, ExecutionEvent evt) =>
        EventReceived?.Invoke(address, evt);

    internal void OnConnectionChanged(string address, bool connected) =>
        ConnectionStateChanged?.Invoke(address, connected);

    public IEnumerable<string> ConnectedAddresses => _connections.Keys;

    public void Dispose()
    {
        foreach (var c in _connections.Values)
            c.Dispose();
        _connections.Clear();
    }

    // ── Single agent connection ────────────────────────────────────────

    private sealed class AgentConnection : IDisposable
    {
        private readonly string _address;
        private readonly AgentConnectionManager _owner;
        private readonly GrpcChannel _channel;
        private CancellationTokenSource? _cts;

        public TestAgentService.TestAgentServiceClient Client { get; }
        public bool IsConnected { get; private set; }

        public AgentConnection(string address, AgentConnectionManager owner)
        {
            _address = address;
            _owner = owner;

            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };

            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true,
            });

            Client = new TestAgentService.TestAgentServiceClient(_channel);
        }

        public void Start(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => StreamEventsLoop(_cts.Token));
        }

        private async Task StreamEventsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var call = Client.SubscribeAgentEvents(new Empty(), cancellationToken: ct);
                    IsConnected = true;
                    _owner.OnConnectionChanged(_address, true);

                    await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
                    {
                        _owner.OnEvent(_address, evt);
                    }
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    IsConnected = false;
                    _owner.OnConnectionChanged(_address, false);
                    try { await Task.Delay(5000, ct); } catch { break; }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    IsConnected = false;
                    _owner.OnConnectionChanged(_address, false);
                    try { await Task.Delay(3000, ct); } catch { break; }
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _channel.Dispose();
        }
    }
}
