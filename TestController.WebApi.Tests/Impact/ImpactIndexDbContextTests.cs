using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class ImpactIndexDbContextTests
{
    [Fact]
    public async Task EnsureCreated_Should_PersistAndRoundtrip_AllIndexEntities()
    {
        // A shared open in-memory connection keeps the schema alive across context instances.
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ImpactIndexDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var ctx = new ImpactIndexDbContext(options))
        {
            await ImpactIndexInitializer.EnsureCreatedAsync(ctx);

            ctx.Documents.Add(new IndexedDocument
            {
                Id = "TC:5678",
                Kind = IndexKind.TestCase,
                WorkItemId = 5678,
                Title = "Login smoke",
                Text = "user can log in",
                Fingerprint = "abc123",
                Revision = 3,
                Length = 4,
                UpdatedUtc = DateTimeOffset.UnixEpoch,
            });
            ctx.DocumentTerms.Add(new DocumentTerm { DocumentId = "TC:5678", Term = "login", TermFrequency = 2 });
            ctx.CorpusStatistics.Add(new CorpusStatistic { Term = "login", DocumentFrequency = 1 });
            ctx.DocumentVectors.Add(new DocumentVector
            {
                DocumentId = "TC:5678",
                Vector = VectorBlob.FromFloats([0.1f, 0.2f, 0.3f]),
                Dimension = 3,
                Model = "test-embed",
            });
            ctx.Metadata.Add(new IndexMetadata { Key = "DocumentCount", Value = "1" });

            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new ImpactIndexDbContext(options))
        {
            IndexedDocument doc = await ctx.Documents.SingleAsync();
            Assert.Equal(IndexKind.TestCase, doc.Kind);
            Assert.Equal(5678, doc.WorkItemId);
            Assert.Equal("Login smoke", doc.Title);

            DocumentVector vector = await ctx.DocumentVectors.SingleAsync();
            Assert.Equal([0.1f, 0.2f, 0.3f], VectorBlob.ToFloats(vector.Vector));
            Assert.Equal(3, vector.Dimension);

            Assert.Equal(1, await ctx.CorpusStatistics.CountAsync());
            Assert.Equal(2, (await ctx.DocumentTerms.SingleAsync()).TermFrequency);
            Assert.Equal("1", (await ctx.Metadata.SingleAsync(m => m.Key == "DocumentCount")).Value);
            Assert.Equal(
                ImpactIndexInitializer.CurrentSchemaVersion.ToString(),
                (await ctx.Metadata.SingleAsync(m => m.Key == ImpactIndexInitializer.SchemaVersionKey)).Value);
        }
    }
}
