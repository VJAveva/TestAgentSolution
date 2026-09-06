using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// The health check is the mechanism that stops a missing index degrading into "no impacted tests found",
/// so every non-Ready branch is asserted, including the message naming the rebuild command.
/// </summary>
public class ImpactIndexHealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "impact-health-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir; a stray handle must not fail the test */ }
    }

    private sealed class FixedPaths : IImpactIndexPathProvider
    {
        public FixedPaths(string root)
        {
            IndexRoot = Path.Combine(root, "ImpactIndex");
            LearningRoot = Path.Combine(root, "Learning");
            Directory.CreateDirectory(IndexRoot);
            Directory.CreateDirectory(LearningRoot);
        }

        public string IndexRoot { get; }
        public string LearningRoot { get; }
        public string IndexFilePath => Path.Combine(IndexRoot, "impact-index.db");
        public string OutcomeFilePath => Path.Combine(LearningRoot, "impact-outcomes.db");
        public bool IndexExists => File.Exists(IndexFilePath);
        public void EnsureIndexRootExists() { }
    }

    private ImpactIndexHealthCheck Build(ImpactMappingOptions? options = null) =>
        new(new FixedPaths(_root), Options.Create(options ?? new ImpactMappingOptions()));

    private void CreateIndex(int documents, DateTimeOffset? builtUtc = null)
    {
        var paths = new FixedPaths(_root);
        using var cn = new SqliteConnection($"Data Source={paths.IndexFilePath}");
        cn.Open();
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IndexedDocuments (Id TEXT PRIMARY KEY);
                CREATE TABLE IndexMetadata (Key TEXT PRIMARY KEY, Value TEXT);
                """;
            cmd.ExecuteNonQuery();
        }
        for (var i = 0; i < documents; i++)
        {
            using var ins = cn.CreateCommand();
            ins.CommandText = "INSERT INTO IndexedDocuments (Id) VALUES ($id);";
            ins.Parameters.AddWithValue("$id", $"doc-{i}");
            ins.ExecuteNonQuery();
        }
        if (builtUtc is { } b)
        {
            using var meta = cn.CreateCommand();
            meta.CommandText = "INSERT INTO IndexMetadata (Key, Value) VALUES ('BuiltUtc', $v);";
            meta.Parameters.AddWithValue("$v", b.ToString("O"));
            meta.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CheckAsync_Should_ReturnMissing_When_FileAbsent()
    {
        var health = await Build().CheckAsync();

        Assert.Equal(ImpactIndexStatus.Missing, health.Status);
        Assert.True(health.IsUnusable);
        Assert.Contains("Rebuild-ImpactIndex.ps1", health.Message);
        Assert.Contains(health.IndexFilePath, health.Message);
    }

    [Fact]
    public async Task CheckAsync_Should_ReturnEmpty_When_NoDocuments()
    {
        CreateIndex(documents: 0);

        var health = await Build().CheckAsync();

        Assert.Equal(ImpactIndexStatus.Empty, health.Status);
        Assert.True(health.IsUnusable);
        Assert.Contains("Rebuild-ImpactIndex.ps1", health.Message);
    }

    [Fact]
    public async Task CheckAsync_Should_ReturnCorrupt_When_FileIsNotSqlite()
    {
        var paths = new FixedPaths(_root);
        File.WriteAllText(paths.IndexFilePath, "this is not a database");

        var health = await Build().CheckAsync();

        Assert.Equal(ImpactIndexStatus.Corrupt, health.Status);
        Assert.True(health.IsUnusable);
    }

    [Fact]
    public async Task CheckAsync_Should_ReturnReady_When_Populated()
    {
        CreateIndex(documents: 5, builtUtc: DateTimeOffset.UtcNow);

        var health = await Build().CheckAsync();

        Assert.Equal(ImpactIndexStatus.Ready, health.Status);
        Assert.True(health.IsReady);
        Assert.False(health.IsUnusable);
        Assert.Equal(5, health.DocumentCount);
    }

    [Fact]
    public async Task CheckAsync_Should_ReturnStale_When_OlderThanThreshold()
    {
        CreateIndex(documents: 3, builtUtc: DateTimeOffset.UtcNow.AddDays(-30));

        var health = await Build(new ImpactMappingOptions { IndexStaleAfter = TimeSpan.FromDays(14) }).CheckAsync();

        Assert.Equal(ImpactIndexStatus.Stale, health.Status);
        // Stale still serves queries — it must not be treated as unusable.
        Assert.False(health.IsUnusable);
        Assert.Equal(3, health.DocumentCount);
    }

    [Fact]
    public async Task CheckAsync_Should_StayReady_When_WithinStaleThreshold()
    {
        CreateIndex(documents: 3, builtUtc: DateTimeOffset.UtcNow.AddDays(-2));

        var health = await Build(new ImpactMappingOptions { IndexStaleAfter = TimeSpan.FromDays(14) }).CheckAsync();

        Assert.Equal(ImpactIndexStatus.Ready, health.Status);
    }

    [Fact]
    public async Task CheckAsync_Should_StayReady_When_MetadataTableAbsent()
    {
        // Regression: querying the DbSet name ("Metadata") instead of the mapped table ("IndexMetadata")
        // reported a populated 1.29 GB index as Corrupt. Build time only drives staleness, so losing it
        // must not condemn the index.
        var paths = new FixedPaths(_root);
        using (var cn = new SqliteConnection($"Data Source={paths.IndexFilePath}"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IndexedDocuments (Id TEXT PRIMARY KEY); INSERT INTO IndexedDocuments (Id) VALUES ('d1');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var health = await Build().CheckAsync();

        Assert.Equal(ImpactIndexStatus.Ready, health.Status);
        Assert.Equal(1, health.DocumentCount);
        Assert.Null(health.LastBuiltUtc);
    }
}
