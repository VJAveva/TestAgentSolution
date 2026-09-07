using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Regression guards for the retrieval snapshot load.
///
/// The production index holds 269k documents and 13.2M postings. Loading a snapshot used to pass every
/// document id of a kind into a <c>WHERE DocumentId IN (...)</c>, which SQLite rejects with
/// "too many SQL variables". <see cref="RegressionImpactMatcher"/> swallowed the error and returned an empty
/// list, so the UI showed "no impacted test cases" for every component. The bug is invisible on a small
/// corpus, so these tests use a document count above SQLite's parameter ceiling on purpose.
/// </summary>
public class RetrievalIndexStoreScaleTests : IDisposable
{
    private const int DocumentsAboveParameterLimit = 40_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "impact-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public RetrievalIndexStoreScaleTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "impact-index.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private sealed class Factory(string path) : IDbContextFactory<ImpactIndexDbContext>
    {
        // Pooling off so the test can rewrite the file without fighting a retained handle.
        public ImpactIndexDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ImpactIndexDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private RetrievalIndexStore Build() =>
        new(new Factory(_dbPath), Options.Create(new ImpactMappingOptions()));

    /// <summary>
    /// Seeds features and test cases. Every document carries "common"; only a slice carries "rare",
    /// so term-scoping has something to exclude.
    /// </summary>
    private void Seed(int testCaseCount, int featureCount = 50, int rareDocs = 5, string builtUtc = "2026-01-01T00:00:00Z")
    {
        using var cn = new SqliteConnection($"Data Source={_dbPath}");
        cn.Open();

        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IndexedDocuments (Id TEXT PRIMARY KEY, Kind INTEGER, WorkItemId INTEGER,
                    Title TEXT, Text TEXT, Fingerprint TEXT, Revision INTEGER, Length INTEGER,
                    ChildCount INTEGER, UpdatedUtc TEXT);
                CREATE TABLE DocumentTerms (DocumentId TEXT, Term TEXT, TermFrequency INTEGER,
                    PRIMARY KEY (DocumentId, Term));
                CREATE TABLE DocumentVectors (DocumentId TEXT PRIMARY KEY, Vector BLOB);
                CREATE TABLE CorpusStatistics (Term TEXT PRIMARY KEY, DocumentFrequency INTEGER);
                CREATE TABLE IndexMetadata (Key TEXT PRIMARY KEY, Value TEXT);
                """;
            cmd.ExecuteNonQuery();
        }

        using var tx = cn.BeginTransaction();

        var doc = cn.CreateCommand();
        doc.CommandText = "INSERT INTO IndexedDocuments (Id, Kind, WorkItemId, Title, Text, Fingerprint, Revision, Length, ChildCount, UpdatedUtc) " +
                          "VALUES ($id, $kind, $wi, '', '', '', 1, 10, $children, '')";
        var term = cn.CreateCommand();
        term.CommandText = "INSERT INTO DocumentTerms (DocumentId, Term, TermFrequency) VALUES ($id, $t, 1)";

        void Add(string id, int kind, int workItemId, int children, params string[] terms)
        {
            doc.Parameters.Clear();
            doc.Parameters.AddWithValue("$id", id);
            doc.Parameters.AddWithValue("$kind", kind);
            doc.Parameters.AddWithValue("$wi", workItemId);
            doc.Parameters.AddWithValue("$children", children);
            doc.ExecuteNonQuery();

            foreach (var t in terms)
            {
                term.Parameters.Clear();
                term.Parameters.AddWithValue("$id", id);
                term.Parameters.AddWithValue("$t", t);
                term.ExecuteNonQuery();
            }
        }

        for (var i = 0; i < featureCount; i++)
            Add($"FT:{i}", 0, 100_000 + i, i, "common");

        for (var i = 0; i < testCaseCount; i++)
            Add($"TC:{i}", 1, i, 0, i < rareDocs ? "rare" : "common");

        using (var meta = cn.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO IndexMetadata (Key, Value) VALUES ('DocumentCount', $c), ('AverageDocumentLength', '10'), ('BuiltUtc', $b)";
            meta.Parameters.AddWithValue("$c", (testCaseCount + featureCount).ToString());
            meta.Parameters.AddWithValue("$b", builtUtc);
            meta.ExecuteNonQuery();
        }

        using (var stats = cn.CreateCommand())
        {
            stats.Transaction = tx;
            stats.CommandText = "INSERT INTO CorpusStatistics (Term, DocumentFrequency) VALUES ('common', $c), ('rare', $r)";
            stats.Parameters.AddWithValue("$c", testCaseCount - rareDocs + featureCount);
            stats.Parameters.AddWithValue("$r", rareDocs);
            stats.ExecuteNonQuery();
        }

        tx.Commit();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_Succeed_When_CorpusExceedsSqlParameterLimit()
    {
        // The original defect: one SQL parameter per document id. Reproduces as
        // "SQLite Error 1: 'too many SQL variables'" before the fix.
        Seed(DocumentsAboveParameterLimit);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.TestCase, ["rare"], CancellationToken.None);

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_LoadOnlyDocumentsCarryingQueryTerms()
    {
        Seed(DocumentsAboveParameterLimit, rareDocs: 5);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.TestCase, ["rare"], CancellationToken.None);

        // 5 of 40,000 - the whole point of term scoping.
        Assert.Equal(5, snapshot.SearchBm25(["rare"], 100).Count);
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_KeepCorpusStatisticsCorpusWide_When_TermScoped()
    {
        Seed(DocumentsAboveParameterLimit, featureCount: 50, rareDocs: 5);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.TestCase, ["rare"], CancellationToken.None);

        // IDF must reflect the whole corpus, never the loaded subset, or scores drift with the query.
        Assert.Equal(DocumentsAboveParameterLimit + 50, snapshot.DocumentCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_ReportCorpusWideMedianChildCount_When_TermScoped()
    {
        // Features 0..49 have ChildCount 0..49; the upper-middle element is 25.
        Seed(testCaseCount: 100, featureCount: 50, rareDocs: 5);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.Feature, ["common"], CancellationToken.None);

        Assert.Equal(25, snapshot.MedianChildCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_ReturnEmpty_When_NoQueryTerms()
    {
        Seed(testCaseCount: 100);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.TestCase, [], CancellationToken.None);

        Assert.Empty(snapshot.SearchBm25(["common"], 100));
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_Succeed_When_LoadingWholeKindAboveParameterLimit()
    {
        // The null-terms path joins on Kind instead of listing ids, so it must also survive a large corpus.
        Seed(DocumentsAboveParameterLimit);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.TestCase, null, CancellationToken.None);

        Assert.Equal(DocumentsAboveParameterLimit - 5, snapshot.SearchBm25(["common"], int.MaxValue).Count);
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_UseCorpusDocumentFrequency_When_TermScoped()
    {
        // Frequencies are now fetched only for the query terms. The VALUE must still be the corpus-wide count,
        // not something derived from the loaded subset, or IDF silently changes with the query.
        Seed(testCaseCount: 1000, featureCount: 50, rareDocs: 5);

        IndexSnapshot snapshot = await Build().GetSnapshotAsync(IndexKind.TestCase, ["rare", "common"], CancellationToken.None);

        Assert.Equal(5, snapshot.DocumentFrequency("rare"));
        Assert.Equal(1000 - 5 + 50, snapshot.DocumentFrequency("common"));
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_ReloadCachedDocuments_When_IndexRebuilt()
    {
        Seed(testCaseCount: 100, rareDocs: 5, builtUtc: "2026-01-01T00:00:00Z");
        RetrievalIndexStore store = Build();

        Assert.Equal(5, (await store.GetSnapshotAsync(IndexKind.TestCase, ["rare"], CancellationToken.None))
            .SearchBm25(["rare"], 100).Count);

        // A rebuild changes BuiltUtc, which is what the document cache is keyed on.
        using (var cn = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = """
                UPDATE DocumentTerms SET Term = 'rare'
                WHERE DocumentId IN ('TC:5','TC:6','TC:7','TC:8');
                UPDATE IndexMetadata SET Value = '2026-02-02T00:00:00Z' WHERE Key = 'BuiltUtc';
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(9, (await store.GetSnapshotAsync(IndexKind.TestCase, ["rare"], CancellationToken.None))
            .SearchBm25(["rare"], 100).Count);
    }
}
