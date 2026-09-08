using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TestController.Persistence;
using TestController.Persistence.Maintenance;
using TestControllerGrpc.Core.Maintenance;

namespace TestController.WebApi.Tests.Maintenance;

/// <summary>
/// The SQLite provider cannot translate ORDER BY over <see cref="DateTimeOffset"/>, so ordering in the query
/// threw <see cref="NotSupportedException"/> on every call. Nothing caught it except the best-effort handler in
/// MaintenanceRecoveryService, so startup recovery silently never ran. These cover both read paths.
/// </summary>
public sealed class MaintenanceOperationStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MaintenanceOperationStore _sut;

    // The store opens and disposes a context per call, so the connection is what has to outlive them.
    private sealed class ConnectionFactory(SqliteConnection connection) : IDbContextFactory<OrchestratorDbContext>
    {
        public OrchestratorDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options);
    }

    public MaintenanceOperationStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var factory = new ConnectionFactory(_connection);
        using (var seed = factory.CreateDbContext())
            seed.Database.EnsureCreated();

        _sut = new MaintenanceOperationStore(factory);
    }

    public void Dispose() => _connection.Dispose();

    private static MaintenanceOperation Op(string node, MaintenanceOperationState state, DateTimeOffset started) => new()
    {
        Id = Guid.NewGuid(),
        NodeId = node,
        Kind = MaintenanceKind.Reboot,
        TriggerSource = MaintenanceTriggerSource.FleetPanel,
        State = state,
        StartedUtc = started,
    };

    [Fact]
    public async Task GetUnfinishedAsync_Should_ReturnQueuedAndRunningOldestFirst_When_OperationsExist()
    {
        var now = DateTimeOffset.UtcNow;
        await _sut.SaveAsync(Op("JVGR2", MaintenanceOperationState.Running, now.AddMinutes(-5)), CancellationToken.None);
        await _sut.SaveAsync(Op("JVGR1", MaintenanceOperationState.Queued, now.AddMinutes(-30)), CancellationToken.None);
        await _sut.SaveAsync(Op("JVHIST", MaintenanceOperationState.Succeeded, now.AddMinutes(-10)), CancellationToken.None);

        var unfinished = await _sut.GetUnfinishedAsync(CancellationToken.None);

        Assert.Equal(["JVGR1", "JVGR2"], unfinished.Select(o => o.NodeId));
    }

    [Fact]
    public async Task GetUnfinishedAsync_Should_ReturnEmpty_When_NoOperationsAreInFlight()
    {
        await _sut.SaveAsync(Op("JVGR1", MaintenanceOperationState.Succeeded, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Empty(await _sut.GetUnfinishedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryAsync_Should_ReturnNewestFirstWithinRange_When_FilteredByNodeAndDate()
    {
        var now = DateTimeOffset.UtcNow;
        await _sut.SaveAsync(Op("JVGR1", MaintenanceOperationState.Succeeded, now.AddDays(-3)), CancellationToken.None);
        await _sut.SaveAsync(Op("JVGR1", MaintenanceOperationState.Failed, now.AddHours(-1)), CancellationToken.None);
        await _sut.SaveAsync(Op("JVGR2", MaintenanceOperationState.Succeeded, now.AddHours(-2)), CancellationToken.None);

        var history = await _sut.GetHistoryAsync("JVGR1", now.AddDays(-1), now, CancellationToken.None);

        Assert.Single(history);
        Assert.Equal(MaintenanceOperationState.Failed, history[0].State);
    }
}
