using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// The staleness check is a read. It once shared the writer's drop-and-recreate path, so a routine
/// fifteen-minute tick after a schema bump emptied a 270k-document production index and left it empty
/// when the rebuild that should have followed failed.
/// </summary>
public sealed class IndexMaintenanceStalenessTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public IndexMaintenanceStalenessTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ImpactIndexDbContext>
    {
        private readonly DbContextOptions<ImpactIndexDbContext> _options =
            new DbContextOptionsBuilder<ImpactIndexDbContext>().UseSqlite(connection).Options;

        public ImpactIndexDbContext CreateDbContext() => new(_options);

        public Task<ImpactIndexDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private IndexMaintenanceService Service(ImpactMappingOptions? options = null)
    {
        IOptions<ImpactMappingOptions> opt = Options.Create(options ?? new ImpactMappingOptions());
        var factory = new Factory(_connection);
        var builder = new RetrievalIndexBuilder(
            factory, new NullAdoWorkItemClient(), NullEmbeddingProvider.Instance,
            opt, new NoopAppLogger());

        return new IndexMaintenanceService(builder, factory, opt, NullLogger<IndexMaintenanceService>.Instance);
    }

    private static IndexedDocument Doc(string id) => new()
    {
        Id = id,
        Kind = IndexKind.TestCase,
        WorkItemId = 1,
        Title = "t",
        Text = "t",
        Fingerprint = "f",
        Revision = 1,
        Length = 1,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
    };

    private async Task SeedAsync(string? schemaVersion, DateTimeOffset? builtUtc)
    {
        await using var ctx = new Factory(_connection).CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Documents.Add(Doc("TC:1"));
        if (schemaVersion is not null)
            ctx.Metadata.Add(new IndexMetadata { Key = ImpactIndexInitializer.SchemaVersionKey, Value = schemaVersion });
        if (builtUtc is { } b)
            ctx.Metadata.Add(new IndexMetadata { Key = "BuiltUtc", Value = b.ToString("O") });
        await ctx.SaveChangesAsync();
    }

    private async Task<int> DocumentCountAsync()
    {
        await using var ctx = new Factory(_connection).CreateDbContext();
        return await ctx.Documents.CountAsync();
    }

    [Fact]
    public async Task IsStaleAsync_Should_NotDropIndex_When_SchemaVersionIsOlder()
    {
        await SeedAsync(schemaVersion: "1", builtUtc: DateTimeOffset.UtcNow);

        await Service().IsStaleAsync(CancellationToken.None);

        Assert.Equal(1, await DocumentCountAsync());
    }

    [Fact]
    public async Task IsStaleAsync_Should_NotDropIndex_When_SchemaVersionAbsent()
    {
        await SeedAsync(schemaVersion: null, builtUtc: DateTimeOffset.UtcNow);

        await Service().IsStaleAsync(CancellationToken.None);

        Assert.Equal(1, await DocumentCountAsync());
    }

    [Fact]
    public async Task IsStaleAsync_Should_ReportStale_When_NeverBuilt()
    {
        await SeedAsync(schemaVersion: ImpactIndexInitializer.CurrentSchemaVersion.ToString(), builtUtc: null);

        Assert.True(await Service().IsStaleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IsStaleAsync_Should_ReportStale_When_OlderThanMaxAge()
    {
        var options = new ImpactMappingOptions();
        options.Index.MaxAge = TimeSpan.FromHours(30);
        await SeedAsync(
            ImpactIndexInitializer.CurrentSchemaVersion.ToString(),
            DateTimeOffset.UtcNow.AddHours(-31));

        Assert.True(await Service(options).IsStaleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IsStaleAsync_Should_ReportFresh_When_WithinMaxAge()
    {
        var options = new ImpactMappingOptions();
        options.Index.MaxAge = TimeSpan.FromHours(30);
        await SeedAsync(
            ImpactIndexInitializer.CurrentSchemaVersion.ToString(),
            DateTimeOffset.UtcNow.AddHours(-1));

        Assert.False(await Service(options).IsStaleAsync(CancellationToken.None));
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067 // event is part of the interface but unused in tests
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}
