using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class ImpactIndexSchemaVersionTests
{
    private static DbContextOptions<ImpactIndexDbContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<ImpactIndexDbContext>().UseSqlite(connection).Options;

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

    [Fact]
    public async Task EnsureCreated_Should_StampCurrentVersion_When_DatabaseIsNew()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var ctx = new ImpactIndexDbContext(OptionsFor(connection));

        await ImpactIndexInitializer.EnsureCreatedAsync(ctx);

        IndexMetadata row = await ctx.Metadata.SingleAsync(m => m.Key == ImpactIndexInitializer.SchemaVersionKey);
        Assert.Equal(ImpactIndexInitializer.CurrentSchemaVersion.ToString(), row.Value);
    }

    [Fact]
    public async Task EnsureCreated_Should_RebuildPreVersioningIndex_When_DocumentTextChanged()
    {
        // An index built before versioning has documents composed without System.Description. Incremental
        // builds only revisit items ADO reports as changed, so stale documents would never be refreshed.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var ctx = new ImpactIndexDbContext(OptionsFor(connection));

        await ctx.Database.EnsureCreatedAsync();
        ctx.Documents.Add(Doc("TC:1"));
        await ctx.SaveChangesAsync();

        await ImpactIndexInitializer.EnsureCreatedAsync(ctx);

        Assert.Equal(0, await ctx.Documents.CountAsync());
        Assert.Equal(
            ImpactIndexInitializer.CurrentSchemaVersion.ToString(),
            (await ctx.Metadata.SingleAsync(m => m.Key == ImpactIndexInitializer.SchemaVersionKey)).Value);
    }

    [Fact]
    public async Task EnsureCreated_Should_KeepStaleIndex_When_CallerIsReader()
    {
        // Readers must not be taken down for the length of a rebuild: a stale index is out of date, not
        // unreadable, and the previous quality beats no impact analysis at all.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var ctx = new ImpactIndexDbContext(OptionsFor(connection));

        await ctx.Database.EnsureCreatedAsync();
        ctx.Documents.Add(Doc("TC:1"));
        await ctx.SaveChangesAsync();

        await ImpactIndexInitializer.EnsureCreatedAsync(ctx, rebuildOnSchemaChange: false);

        Assert.Equal(1, await ctx.Documents.CountAsync());
    }
}
