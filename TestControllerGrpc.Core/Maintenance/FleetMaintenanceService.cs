using System.Collections.Concurrent;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// The fleet-facing façade every front door calls. Guards against duplicate operations per node (TryAdd is the
/// guard, mirroring how ExecutionSessionManager works), runs each operation on a background task, and re-raises the
/// engine's progress/completion as events for hosts to project. (Spec: FleetRevert §5, Prompt 5.)
/// </summary>
public sealed class FleetMaintenanceService : IFleetMaintenanceService
{
    private const string LogCategory = "Maintenance.Fleet";

    private readonly IMachineRevertOperation _revertOperation;
    private readonly IMachineRebootOperation _rebootOperation;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;
    private readonly IMaintenanceStateStore _stateStore;
    private readonly IMaintenanceOperationStore _operationStore;
    private readonly IUpdatePolicyStore _policy;
    private readonly IAppLogger _logger;

    private readonly ConcurrentDictionary<string, RunningOperation> _active = new(StringComparer.OrdinalIgnoreCase);
    // Work over its kind's cap waits here rather than being rejected (spec Prompt 17). Queues are per-kind so a
    // saturated refresh queue cannot head-of-line block a reboot that has a free slot.
    private readonly ConcurrentDictionary<MaintenanceKind, ConcurrentQueue<string>> _queues = new();
    // Serialises the count-then-start decision; without it two callers can both read "under cap" and both start.
    private readonly object _startGate = new();

    public event EventHandler<MaintenanceProgress>? ProgressChanged;
    public event EventHandler<MaintenanceOperation>? OperationCompleted;

    public FleetMaintenanceService(
        IMachineRevertOperation revertOperation,
        IMachineRebootOperation rebootOperation,
        IAgentGrpcDispatcher dispatcher,
        AgentLockManager lockManager,
        IMaintenanceStateStore stateStore,
        IMaintenanceOperationStore operationStore,
        IUpdatePolicyStore policy,
        IAppLogger logger)
    {
        _revertOperation = revertOperation;
        _rebootOperation = rebootOperation;
        _dispatcher = dispatcher;
        _lockManager = lockManager;
        _stateStore = stateStore;
        _operationStore = operationStore;
        _policy = policy;
        _logger = logger;
    }

    public IReadOnlyCollection<MaintenanceOperation> ActiveOperations
        => _active.Values.Select(r => r.Operation).ToArray();

    public Task<PrecheckResult> PrecheckAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken)
    {
        var results = new List<NodePrecheck>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            var state = _stateStore.Get(nodeId);
            var lockInfo = _lockManager.GetLock(nodeId);
            var registered = _dispatcher.RegisteredAgents.Any(a => string.Equals(a, nodeId, StringComparison.OrdinalIgnoreCase));
            var eligibility = DispatchGate.Evaluate(_stateStore, _lockManager, nodeId, registered);
            results.Add(new NodePrecheck(
                nodeId,
                eligibility == DispatchEligibility.Eligible,
                state,
                lockInfo?.WatchItemTag,
                eligibility));
        }

        return Task.FromResult(new PrecheckResult(results));
    }

    public Task<Guid> StartRevertAsync(RevertRequest request, CancellationToken cancellationToken)
    {
        var operation = new MaintenanceOperation
        {
            Id = Guid.NewGuid(),
            NodeId = request.NodeId,
            Kind = MaintenanceKind.Revert,
            SnapshotName = request.SnapshotName,
            ScriptPath = request.ScriptPath,
            State = MaintenanceOperationState.Queued,
            TriggerSource = request.TriggerSource,
            TriggeredBy = request.TriggeredBy,
            Reason = request.Reason,
            LinkedRunId = request.LinkedRunId,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new RunningOperation(operation, cts)
        {
            Execute = (op, prog, ct) => _revertOperation.ExecuteAsync(op, request, prog, ct),
        };

        // TryAdd failing IS the duplicate-operation check — no separate lock needed.
        if (!_active.TryAdd(request.NodeId, running))
        {
            cts.Dispose();
            var existing = _active.TryGetValue(request.NodeId, out var current) ? current.Operation.Id : Guid.Empty;
            throw new MaintenanceInProgressException(request.NodeId, existing);
        }

        EnqueueOrStart(request.NodeId, running);
        return Task.FromResult(operation.Id);
    }

    public Task<Guid> StartRebootAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        var operation = new MaintenanceOperation
        {
            Id = Guid.NewGuid(),
            NodeId = request.NodeId,
            Kind = MaintenanceKind.Reboot,
            State = MaintenanceOperationState.Queued,
            TriggerSource = request.TriggerSource,
            TriggeredBy = request.TriggeredBy,
            Reason = request.Reason,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new RunningOperation(operation, cts)
        {
            Execute = (op, prog, ct) => _rebootOperation.ExecuteAsync(op, request, prog, ct),
        };

        if (!_active.TryAdd(request.NodeId, running))
        {
            cts.Dispose();
            var existing = _active.TryGetValue(request.NodeId, out var current) ? current.Operation.Id : Guid.Empty;
            throw new MaintenanceInProgressException(request.NodeId, existing);
        }

        EnqueueOrStart(request.NodeId, running);
        return Task.FromResult(operation.Id);
    }

    /// <summary>Cap for a kind, or <see cref="int.MaxValue"/> when the kind is not throttled.</summary>
    private int CapFor(MaintenanceKind kind) => kind switch
    {
        MaintenanceKind.Reboot => Math.Max(1, _policy.Current.MaxConcurrentReboots),
        MaintenanceKind.GoldenImageRefresh => Math.Max(1, _policy.Current.MaxConcurrentRefreshes),
        _ => int.MaxValue,
    };

    private int RunningCount(MaintenanceKind kind)
        => _active.Values.Count(r => r.Started && r.Operation.Kind == kind);

    private ConcurrentQueue<string> QueueFor(MaintenanceKind kind)
        => _queues.GetOrAdd(kind, _ => new ConcurrentQueue<string>());

    // Over the concurrency cap the operation stays Queued and waits its turn — never rejected.
    private void EnqueueOrStart(string nodeId, RunningOperation running)
    {
        var kind = running.Operation.Kind;
        lock (_startGate)
        {
            var cap = CapFor(kind);
            if (RunningCount(kind) >= cap)
            {
                QueueFor(kind).Enqueue(nodeId);
                _logger.Info(LogCategory, $"{kind} for '{nodeId}' queued behind the {cap}-operation limit.");
                return;
            }

            StartRunning(nodeId, running);
        }
    }

    private void StartRunning(string nodeId, RunningOperation running)
    {
        running.Started = true;
        _ = Task.Run(() => RunAsync(nodeId, running, running.Execute!), CancellationToken.None);
    }

    // Called once a slot frees up. Skips entries whose node is no longer waiting (cancelled or already gone).
    private void PumpQueues()
    {
        lock (_startGate)
        {
            foreach (var kind in _queues.Keys)
            {
                var cap = CapFor(kind);
                var queue = QueueFor(kind);
                // Counted once per kind, not per iteration: RunningCount enumerates _active.Values, which
                // snapshots the dictionary into a new array on every call.
                var running = RunningCount(kind);
                while (running < cap && queue.TryDequeue(out var nodeId))
                {
                    if (_active.TryGetValue(nodeId, out var waiting) && !waiting.Started && waiting.Execute is not null)
                    {
                        StartRunning(nodeId, waiting);
                        running++;
                    }
                }
            }
        }
    }

    private async Task RunAsync(
        string nodeId,
        RunningOperation running,
        Func<MaintenanceOperation, IProgress<MaintenanceProgress>, CancellationToken, Task<MaintenanceOperation>> execute)
    {
        MaintenanceOperation result;
        try
        {
            var progress = new SyncProgress<MaintenanceProgress>(p =>
            {
                running.Update(p);
                ProgressChanged?.Invoke(this, p);
            });

            result = await execute(running.Operation, progress, running.Cancellation.Token);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory, $"Maintenance operation for '{nodeId}' faulted.", ex);

            // The operations quarantine in their own handlers. If one throws past that, the node would keep
            // claiming Rebooting/Reverting - which DispatchGate blocks and nothing re-evaluates until the
            // controller restarts. Only transient states are converted, so a precheck fault (state still
            // None) does not earn a node a quarantine it never needed.
            if (_stateStore.Get(nodeId) is MaintenanceState.Rebooting
                or MaintenanceState.Reverting or MaintenanceState.Updating)
            {
                _stateStore.Set(nodeId, MaintenanceState.Quarantined);
                _logger.Warn(LogCategory, $"'{nodeId}' quarantined: the operation faulted while the node was in-flight.");
            }

            result = running.Operation with
            {
                State = MaintenanceOperationState.Failed,
                FailurePhase = running.Operation.Phase,
                CompletedUtc = DateTimeOffset.UtcNow,
            };
            try { await _operationStore.SaveAsync(result, CancellationToken.None); } catch { /* best-effort */ }
        }
        finally
        {
            _active.TryRemove(nodeId, out _);
            running.Cancellation.Dispose();
            PumpQueues();
        }

        // Raised after the node is out of the active map, so a completion handler can immediately re-queue it.
        OperationCompleted?.Invoke(this, result);
    }

    public Task<bool> RequestCancelAsync(Guid operationId)
    {
        foreach (var running in _active.Values)
        {
            if (running.Operation.Id == operationId)
            {
                running.Cancellation.Cancel();  // honored at the next phase boundary; refused during SnapshotRevert
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task ClearQuarantineAsync(string nodeId, string clearedBy)
    {
        if (_stateStore.Get(nodeId) == MaintenanceState.Quarantined)
        {
            _stateStore.Set(nodeId, MaintenanceState.None);
            _logger.Info(LogCategory, $"Quarantine cleared on node '{nodeId}' by {clearedBy}.");
        }

        return Task.CompletedTask;
    }

    private sealed class RunningOperation
    {
        private MaintenanceOperation _operation;

        public RunningOperation(MaintenanceOperation operation, CancellationTokenSource cancellation)
        {
            _operation = operation;
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }

        /// <summary>False while the operation sits behind its kind's concurrency cap.</summary>
        public bool Started { get; set; }

        /// <summary>Deferred body, so a queued operation can be launched later without re-capturing the request.</summary>
        public Func<MaintenanceOperation, IProgress<MaintenanceProgress>, CancellationToken, Task<MaintenanceOperation>>? Execute { get; init; }

        public MaintenanceOperation Operation => Volatile.Read(ref _operation);

        public void Update(MaintenanceProgress progress)
        {
            var current = Volatile.Read(ref _operation);
            Volatile.Write(ref _operation, current with { Phase = progress.Phase, State = MaintenanceOperationState.Running });
        }
    }
}

/// <summary>
/// Invokes the handler on the reporting thread. <see cref="Progress{T}"/> posts to the captured
/// synchronization context, which defers — and for script output that means lines arrive after the run has
/// already been judged.
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
