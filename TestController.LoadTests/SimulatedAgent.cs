using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestAgentGrpc;

namespace TestController.LoadTests;

/// <summary>
/// One simulated agent: a Kestrel-hosted <c>TestAgentService</c> gRPC server on a
/// localhost port, plus a <c>TestControllerService</c> client that registers with
/// the controller and heartbeats on the production cadence.
/// </summary>
public sealed class SimulatedAgent : IAsyncDisposable
{
    private readonly LoadTestOptions _opts;
    private readonly int _port;
    private readonly string _endpoint;
    private WebApplication? _app;
    private GrpcChannel? _controllerChannel;
    private TestControllerService.TestControllerServiceClient? _controller;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatLoop;

    public string Name { get; }
    public long HeartbeatsSent { get; private set; }
    public long HeartbeatFailures { get; private set; }

    public SimulatedAgent(LoadTestOptions opts, int index)
    {
        _opts = opts;
        _port = opts.BasePort + index;
        Name = $"sim-agent-{index:D4}";
        _endpoint = $"http://{opts.AgentHost}:{_port}";
    }

    /// <summary>Starts the gRPC server and registers with the controller.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep the harness console quiet at fleet scale
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Plaintext HTTP/2 (h2c) — matches how the controller dials agents.
            k.ListenLocalhost(_port, o => o.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(new SimulatedAgentService(Name, _opts.OutputLinesPerSecond));

        _app = builder.Build();
        _app.MapGrpcService<SimulatedAgentService>();
        await _app.StartAsync(ct);

        _controllerChannel = GrpcChannel.ForAddress(_opts.ControllerGrpcUrl);
        _controller = new TestControllerService.TestControllerServiceClient(_controllerChannel);

        await _controller.RegisterAsync(new TestAgentRef
        {
            Name = Name,
            State = AgentState.Ready,
            Endpoint = _endpoint,
        }, cancellationToken: ct);

        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeatLoop = RunHeartbeatLoopAsync(_heartbeatCts.Token);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_opts.HeartbeatInterval, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                await _controller!.HeartbeatAsync(new HeartbeatRequest
                {
                    AgentName = Name,
                    State = AgentState.Ready,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Metrics = new ResourceMetrics
                    {
                        CpuUsagePct = 5,
                        MemoryUsedMb = 512,
                        MemoryTotalMb = 8192,
                        ActiveProcessCount = 1,
                        OsDescription = "loadtest-sim",
                    },
                }, cancellationToken: ct);
                HeartbeatsSent++;
            }
            catch (OperationCanceledException) { break; }
            catch { HeartbeatFailures++; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_heartbeatCts is not null)
        {
            await _heartbeatCts.CancelAsync();
            if (_heartbeatLoop is not null)
            {
                try { await _heartbeatLoop; } catch { /* shutdown */ }
            }
            _heartbeatCts.Dispose();
        }

        try
        {
            if (_controller is not null)
            {
                await _controller.UnRegisterAsync(new TestAgentRef { Name = Name, Endpoint = _endpoint });
            }
        }
        catch { /* best-effort unregister */ }

        _controllerChannel?.Dispose();

        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* shutdown */ }
            await _app.DisposeAsync();
        }
    }
}
