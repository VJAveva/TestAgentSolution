using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TestController.Persistence;
using TestController.Persistence.Maintenance;
using TestControllerGrpc.Core.Maintenance;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// Crash recovery reads GetUnfinishedAsync from this store, so a wrong answer here silently decides whether
/// an interrupted node is ever reconciled. The store had no tests at all before this.
/// </summary>
public class MaintenanceOperationStoreTests : IDisposable
{
    private const string Node = "JVGR2";

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly MaintenanceOperationStore _store;

    public MaintenanceOperationStoreTests()
    {
        // Shared in-memory database: the store opens a fresh context per call, so the connection must outlive them.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        using (var seed = new OrchestratorDbContext(options)) seed.Database.EnsureCreated();

        _factory = new TestDbContextFactory(options);
        _store = new MaintenanceOperationStore(_factory);
    }

    public void Dispose() => _connection.Dispose();

    private static MaintenanceOperation Operation(
        Guid id, MaintenanceOperationState state, MaintenanceKind kind = MaintenanceKind.Reboot, string node = Node) =>
        new()
        {
            Id = id,
            NodeId = node,
            Kind = kind,
            State = state,
            TriggerSource = MaintenanceTriggerSource.FleetPanel,
            TriggeredBy = "wwApps",
            StartedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SaveAsync_Should_RoundTripTheOperation_When_ItIsNew()
    {
        var id = Guid.NewGuid();

        await _store.SaveAsync(Operation(id, MaintenanceOperationState.Running), CancellationToken.None);
        var loaded = await _store.GetAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(Node, loaded!.NodeId);
        Assert.Equal(MaintenanceKind.Reboot, loaded.Kind);
        Assert.Equal(MaintenanceOperationState.Running, loaded.State);
    }

    [Fact]
    public async Task SaveAsync_Should_Upsert_When_TheSameOperationAdvancesAPhase()
    {
        // The engine persists the same id repeatedly as it moves through phases; a second insert would throw.
        var id = Guid.NewGuid();

        await _store.SaveAsync(Operation(id, MaintenanceOperationState.Running), CancellationToken.None);
        await _store.SaveAsync(
            Operation(id, MaintenanceOperationState.Succeeded) with { CompletedUtc = DateTimeOffset.UtcNow },
            CancellationToken.None);

        var loaded = await _store.GetAsync(id, CancellationToken.None);
        Assert.Equal(MaintenanceOperationState.Succeeded, loaded!.State);

        var history = await _store.GetHistoryAsync(Node, null, null, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task GetUnfinishedAsync_Should_ReturnInterruptedOperations_When_TheyNeverCompleted()
    {
        var running = Guid.NewGuid();
        var queued = Guid.NewGuid();
        await _store.SaveAsync(Operation(running, MaintenanceOperationState.Running), CancellationToken.None);
        await _store.SaveAsync(Operation(queued, MaintenanceOperationState.Queued, node: "WARMGR"), CancellationToken.None);

        var orphans = await _store.GetUnfinishedAsync(CancellationToken.None);

        Assert.Equal(2, orphans.Count);
        Assert.Contains(orphans, o => o.Id == running);
        Assert.Contains(orphans, o => o.Id == queued);
    }

    [Theory]
    [InlineData(MaintenanceOperationState.Succeeded)]
    [InlineData(MaintenanceOperationState.Failed)]
    [InlineData(MaintenanceOperationState.Cancelled)]
    public async Task GetUnfinishedAsync_Should_IgnoreFinishedOperations_When_TheyReachedATerminalState(
        MaintenanceOperationState terminal)
    {
        // A terminal operation resurfacing as an orphan would quarantine a healthy node on every restart.
        await _store.SaveAsync(
            Operation(Guid.NewGuid(), terminal) with { CompletedUtc = DateTimeOffset.UtcNow },
            CancellationToken.None);

        Assert.Empty(await _store.GetUnfinishedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_Should_ReturnNull_When_TheOperationIsUnknown()
    {
        Assert.Null(await _store.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private sealed class TestDbContextFactory(DbContextOptions<OrchestratorDbContext> options)
        : IDbContextFactory<OrchestratorDbContext>
    {
        public OrchestratorDbContext CreateDbContext() => new(options);

        public Task<OrchestratorDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
