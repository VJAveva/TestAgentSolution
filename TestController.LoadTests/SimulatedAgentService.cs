using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TestAgentGrpc;

namespace TestController.LoadTests;

/// <summary>
/// Lightweight in-process implementation of <c>TestAgentService</c> used by the
/// load harness. Mirrors the production agent's streaming contract closely enough
/// that the controller exercises its real <c>RemoteCommandStreamRunner</c> path:
/// on <see cref="RunCommandStreamed"/> it emits EVENT_STARTED, a configurable rate
/// of EVENT_STDOUT_LINE events, then EVENT_COMPLETED with exit code 0.
/// </summary>
public sealed class SimulatedAgentService : TestAgentService.TestAgentServiceBase
{
    private readonly string _agentName;
    private readonly int _outputLinesPerSecond;
    private int _running; // 0 = ready, 1 = running
    private long _executionsCompleted;

    public SimulatedAgentService(string agentName, int outputLinesPerSecond)
    {
        _agentName = agentName;
        _outputLinesPerSecond = Math.Max(1, outputLinesPerSecond);
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public override Task<StateReply> GetState(Empty request, ServerCallContext context)
        => Task.FromResult(new StateReply
        {
            State = IsRunning ? AgentState.Running : AgentState.Ready,
        });

    public override Task<Empty> ForceReady(Empty request, ServerCallContext context)
    {
        Interlocked.Exchange(ref _running, 0);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> TerminateExecution(Empty request, ServerCallContext context)
    {
        Interlocked.Exchange(ref _running, 0);
        return Task.FromResult(new Empty());
    }

    public override async Task RunCommandStreamed(
        RunCommandRequest request,
        IServerStreamWriter<ExecutionEvent> responseStream,
        ServerCallContext context)
    {
        // Reject if already running (mirrors the production busy contract).
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            await responseStream.WriteAsync(new ExecutionEvent
            {
                ExecutionId = request.ExecutionId ?? "rejected",
                AgentName = _agentName,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                EventType = ExecutionEventType.EventFailed,
                ErrorMessage = "Agent is busy.",
                Detail = "Command rejected — another execution is in progress.",
            });
            return;
        }

        try
        {
            var execId = string.IsNullOrEmpty(request.ExecutionId)
                ? Guid.NewGuid().ToString("N")
                : request.ExecutionId;

            await responseStream.WriteAsync(new ExecutionEvent
            {
                ExecutionId = execId,
                AgentName = _agentName,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                EventType = ExecutionEventType.EventStarted,
                Command = request.Command,
                Arguments = request.Arguments,
            });

            // Stream stdout at a realistic rate for the requested duration
            // (TimeoutSeconds doubles as the simulated run length; default 5s).
            var runLength = request.TimeoutSeconds > 0
                ? TimeSpan.FromSeconds(Math.Min(request.TimeoutSeconds, 600))
                : TimeSpan.FromSeconds(5);
            var perLineDelay = TimeSpan.FromSeconds(1.0 / _outputLinesPerSecond);
            var deadline = DateTime.UtcNow + runLength;
            long line = 0;

            while (DateTime.UtcNow < deadline && !context.CancellationToken.IsCancellationRequested)
            {
                await responseStream.WriteAsync(new ExecutionEvent
                {
                    ExecutionId = execId,
                    AgentName = _agentName,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    EventType = ExecutionEventType.EventStdoutLine,
                    OutputLine = $"[{_agentName}] simulated output line {++line}",
                    OutputKind = OutputKind.OutputStdout,
                });

                try { await Task.Delay(perLineDelay, context.CancellationToken); }
                catch (OperationCanceledException) { break; }
            }

            await responseStream.WriteAsync(new ExecutionEvent
            {
                ExecutionId = execId,
                AgentName = _agentName,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                EventType = ExecutionEventType.EventCompleted,
                ExitCode = 0,
            });

            Interlocked.Increment(ref _executionsCompleted);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — treat as normal teardown.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public override Task<AgentSnapshot> GetAgentSnapshot(Empty request, ServerCallContext context)
        => Task.FromResult(new AgentSnapshot
        {
            AgentName = _agentName,
            State = IsRunning ? AgentState.Running : AgentState.Ready,
            CurrentActivity = IsRunning ? "Running simulated command" : "Idle",
            ExecutionsCompleted = (int)Interlocked.Read(ref _executionsCompleted),
            ExecutionsFailed = 0,
            AgentVersion = "loadtest-sim",
        });
}
