using Microsoft.Extensions.Logging;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// A crash mid-revert loses the in-memory MaintenanceState, so without recovery the node reads as None and
/// becomes dispatchable while its VM may still be reverting - producing test failures that look like product
/// bugs. These assert the node is quarantined instead and the orphaned row is closed out.
/// </summary>
public class MaintenanceRecoveryServiceTests
{
    private const string Node = "JVKPRI";

    private sealed class FakeStore : IMaintenanceOperationStore
    {
        private readonly List<MaintenanceOperation> _unfinished;
        public List<MaintenanceOperation> Saved { get; } = new();

        public FakeStore(params MaintenanceOperation[] unfinished) => _unfinished = unfinished.ToList();

        public Task SaveAsync(MaintenanceOperation operation, CancellationToken ct)
        {
            Saved.Add(operation);
            return Task.CompletedTask;
        }

        public Task<MaintenanceOperation?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<MaintenanceOperation?>(_unfinished.FirstOrDefault(o => o.Id == id));

        public Task<IReadOnlyList<MaintenanceOperation>> GetHistoryAsync(
            string? nodeId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MaintenanceOperation>>([]);

        public Task<IReadOnlyList<MaintenanceOperation>> GetUnfinishedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MaintenanceOperation>>(_unfinished);
    }

    private sealed class ThrowingStore : IMaintenanceOperationStore
    {
        public Task SaveAsync(MaintenanceOperation operation, CancellationToken ct) => Task.CompletedTask;
        public Task<MaintenanceOperation?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<MaintenanceOperation?>(null);
        public Task<IReadOnlyList<MaintenanceOperation>> GetHistoryAsync(
            string? nodeId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MaintenanceOperation>>([]);
        public Task<IReadOnlyList<MaintenanceOperation>> GetUnfinishedAsync(CancellationToken ct) =>
            throw new InvalidOperationException("database unavailable");
    }

    private static MaintenanceOperation Running(string nodeId, MaintenanceKind kind = MaintenanceKind.Revert) => new()
    {
        Id = Guid.NewGuid(),
        NodeId = nodeId,
        Kind = kind,
        State = MaintenanceOperationState.Running,
        Phase = RevertPhase.SnapshotRevert,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
    };

    private static async Task<FakeAppLogger> RunAsync(IMaintenanceOperationStore store, IMaintenanceStateStore state)
    {
        var logger = new FakeAppLogger();
        var svc = new MaintenanceRecoveryService(store, state, logger);
        await svc.RecoverAsync(CancellationToken.None);
        return logger;
    }

    private sealed class FakeAppLogger : IAppLogger
    {
        public List<string> Errors { get; } = new();
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) => Errors.Add($"{message} :: {ex}");
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }

    [Fact]
    public async Task Recovery_Should_QuarantineNode_When_OperationInterrupted()
    {
        var op = Running(Node);
        var store = new FakeStore(op);
        var state = new MaintenanceStateStore();

        await RunAsync(store, state);

        // Not None: a half-reverted machine must not silently rejoin the dispatchable pool.
        Assert.Equal(MaintenanceState.Quarantined, state.Get(Node));
    }

    [Fact]
    public async Task Recovery_Should_CloseOrphanedOperationAsFailed()
    {
        var op = Running(Node);
        var store = new FakeStore(op);

        var logger = await RunAsync(store, new MaintenanceStateStore());

        Assert.Empty(logger.Errors);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(op.Id, saved.Id);
        Assert.Equal(MaintenanceOperationState.Failed, saved.State);
        Assert.Equal(RevertPhase.SnapshotRevert, saved.FailurePhase);
        Assert.NotNull(saved.CompletedUtc);
    }

    [Fact]
    public async Task Recovery_Should_HandleEveryOrphan_When_MultipleNodesInterrupted()
    {
        var store = new FakeStore(Running("NodeA"), Running("NodeB", MaintenanceKind.Reboot));
        var state = new MaintenanceStateStore();

        await RunAsync(store, state);

        Assert.Equal(MaintenanceState.Quarantined, state.Get("NodeA"));
        Assert.Equal(MaintenanceState.Quarantined, state.Get("NodeB"));
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task Recovery_Should_DoNothing_When_NoOrphans()
    {
        var store = new FakeStore();
        var state = new MaintenanceStateStore();

        await RunAsync(store, state);

        Assert.Empty(store.Saved);
        Assert.Equal(MaintenanceState.None, state.Get(Node));
    }

    [Fact]
    public async Task Recovery_Should_NotBlockStartup_When_StoreThrows()
    {
        // A recovery failure must never stop the controller from starting.
        await RunAsync(new ThrowingStore(), new MaintenanceStateStore());
    }
}
