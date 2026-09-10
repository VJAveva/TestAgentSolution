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
    public async Task EnsureCreated_Should_AdoptExistingIndex_When_VersionIsAbsent()
    {
        // A pre-versioning production index must be stamped, never dropped — its shape IS version 1 and a
        // rebuild is a multi-hour ADO crawl.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var ctx = new ImpactIndexDbContext(OptionsFor(connection));

        await ctx.Database.EnsureCreatedAsync();
        ctx.Documents.Add(Doc("TC:1"));
        await ctx.SaveChangesAsync();

        await ImpactIndexInitializer.EnsureCreatedAsync(ctx);

        Assert.Equal(1, await ctx.Documents.CountAsync());
        Assert.Equal(
            ImpactIndexInitializer.CurrentSchemaVersion.ToString(),
            (await ctx.Metadata.SingleAsync(m => m.Key == ImpactIndexInitializer.SchemaVersionKey)).Value);
    }

    [Fact]
    public async Task EnsureCreated_Should_Throw_When_VersionMismatchAndRebuildDisabled()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var ctx = new ImpactIndexDbContext(OptionsFor(connection));

        await ctx.Database.EnsureCreatedAsync();
        ctx.Metadata.Add(new IndexMetadata { Key = ImpactIndexInitializer.SchemaVersionKey, Value = "999" });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ImpactIndexInitializer.EnsureCreatedAsync(ctx, rebuildOnSchemaChange: false));
    }
}
