using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>Loads kind-scoped searchable <see cref="IndexSnapshot"/> instances from the persisted index (P24).</summary>
public interface IRetrievalIndexStore
{
    /// <summary>
    /// Materialises an in-memory snapshot of the documents of one kind, with corpus-wide statistics.
    /// </summary>
    /// <param name="terms">
    /// Query terms to scope the load to. Only documents carrying at least one of these terms are loaded, which
    /// is exactly the set BM25 can score above zero — so this is a cost reduction, not an approximation.
    /// Null loads every document of the kind, which is only viable on small corpora.
    /// </param>
    Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct);
}

/// <summary>
/// Bridges the persisted SQLite index to the in-memory <see cref="IndexSnapshot"/> the retriever searches
/// (P24). Documents are loaded kind-scoped (features or test cases) so searches never mix kinds, but corpus
/// statistics (document count, average length and term document frequencies) are loaded corpus-wide so IDF
/// stays a true global rarity measure.
/// </summary>
public sealed class RetrievalIndexStore : IRetrievalIndexStore
{
    // Well under SQLite's variable ceiling; the "too many SQL variables" failure this guards against only
    // appears once the corpus is large, which is exactly when it is hardest to reproduce.
    private const int TermsPerQuery = 400;
    private const int IdsPerQuery = 400;

    private readonly IDbContextFactory<ImpactIndexDbContext> _contextFactory;
    private readonly ImpactMappingOptions.RetrievalOptions _retrieval;
    private readonly IImpactIndexHealthCheck? _health;

    // Per-kind document table and corpus scalars, rebuilt only when the index is. Singleton-scoped, so one
    // load serves every component in a MatchManyAsync fan-out instead of one per call.
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private string? _cachedVersion;
    private readonly Dictionary<IndexKind, KindCache> _kindCaches = [];

    private readonly record struct DocRow(int WorkItemId, int Length, int ChildCount);
    private sealed record KindCache(Dictionary<string, DocRow> DocsById, int MedianChildCount);

    /// <summary>Creates the store over the index database context factory.</summary>
    /// <param name="health">
    /// When supplied, the index is verified before every read. Without it <c>EnsureCreatedAsync</c> would
    /// silently materialise an empty database and every query would return zero results — indistinguishable
    /// from a genuine "no impacted tests". Optional so existing unit tests can construct the store directly.
    /// </param>
    public RetrievalIndexStore(
        IDbContextFactory<ImpactIndexDbContext> contextFactory,
        IOptions<ImpactMappingOptions> options,
        IImpactIndexHealthCheck? health = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _retrieval = options.Value.Retrieval;
        _health = health;
    }

    /// <inheritdoc />
    public async Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, IReadOnlyCollection<string>? terms, CancellationToken ct)
    {
        if (_health is not null)
        {
            ImpactIndexHealth health = await _health.CheckAsync(ct).ConfigureAwait(false);
            if (health.IsUnusable)
                throw new ImpactIndexUnavailableException(health);
        }

        await using ImpactIndexDbContext ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Readers never drop the index (single-writer invariant); a version mismatch throws instead.
        await ImpactIndexInitializer.EnsureCreatedAsync(ctx, rebuildOnSchemaChange: false, ct).ConfigureAwait(false);

        Dictionary<string, string> metadata = await ctx.Metadata
            .ToDictionaryAsync(m => m.Key, m => m.Value, ct)
            .ConfigureAwait(false);
        int corpusDocumentCount = ParseInt(metadata, "DocumentCount");
        double averageLength = ParseDouble(metadata, "AverageDocumentLength", 1.0);

        if (terms is null)
        {
            // Legacy whole-kind load, kept for small corpora and fixtures.
            Dictionary<string, int> allFrequencies = await ctx.CorpusStatistics
                .ToDictionaryAsync(c => c.Term, c => c.DocumentFrequency, ct)
                .ConfigureAwait(false);
            List<SnapshotDocument> all = await LoadAllDocumentsAsync(ctx, kind, ct).ConfigureAwait(false);
            int n = corpusDocumentCount > 0 ? corpusDocumentCount : all.Count;
            return new IndexSnapshot(
                n, averageLength, allFrequencies, all,
                _retrieval.Bm25K1, _retrieval.Bm25B,
                await MedianChildCountAsync(ctx, kind, ct).ConfigureAwait(false));
        }

        string[] distinct = terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        KindCache kindCache = await GetKindCacheAsync(ctx, kind, metadata, ct).ConfigureAwait(false);

        // Only the query terms' frequencies are ever read: Bm25Scorer resolves IDF per query term and nothing
        // else consults the table. Loading all 145k corpus rows per call was pure waste.
        Dictionary<string, int> documentFrequencies = await LoadFrequenciesAsync(ctx, distinct, ct).ConfigureAwait(false);

        List<SnapshotDocument> snapshotDocs = distinct.Length == 0
            ? []
            : await LoadTermScopedDocumentsAsync(ctx, kind, distinct, kindCache, ct).ConfigureAwait(false);

        int documentCount = corpusDocumentCount > 0 ? corpusDocumentCount : kindCache.DocsById.Count;
        return new IndexSnapshot(
            documentCount, averageLength, documentFrequencies, snapshotDocs,
            _retrieval.Bm25K1, _retrieval.Bm25B, kindCache.MedianChildCount);
    }

    /// <summary>
    /// Document table plus corpus-wide median for one kind, cached against the index's BuiltUtc so a rebuild
    /// invalidates it. Without a BuiltUtc there is nothing safe to key on, so the data is rebuilt each call.
    /// </summary>
    private async Task<KindCache> GetKindCacheAsync(
        ImpactIndexDbContext ctx, IndexKind kind, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        string? version = metadata.GetValueOrDefault("BuiltUtc");
        if (version is null)
            return await BuildKindCacheAsync(ctx, kind, ct).ConfigureAwait(false);

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedVersion != version)
            {
                _kindCaches.Clear();
                _cachedVersion = version;
            }

            if (_kindCaches.TryGetValue(kind, out KindCache? cached))
                return cached;

            KindCache built = await BuildKindCacheAsync(ctx, kind, ct).ConfigureAwait(false);
            _kindCaches[kind] = built;
            return built;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static async Task<KindCache> BuildKindCacheAsync(ImpactIndexDbContext ctx, IndexKind kind, CancellationToken ct)
    {
        List<KeyValuePair<string, DocRow>> rows = await ctx.Documents
            .Where(d => d.Kind == kind)
            .Select(d => new KeyValuePair<string, DocRow>(d.Id, new DocRow(d.WorkItemId, d.Length, d.ChildCount)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new KindCache(
            new Dictionary<string, DocRow>(rows, StringComparer.Ordinal),
            await MedianChildCountAsync(ctx, kind, ct).ConfigureAwait(false));
    }

    private static async Task<Dictionary<string, int>> LoadFrequenciesAsync(
        ImpactIndexDbContext ctx, string[] terms, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string[] chunk in Chunk(terms, TermsPerQuery))
        {
            List<CorpusStatistic> batch = await ctx.CorpusStatistics
                .Where(c => chunk.Contains(c.Term))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (CorpusStatistic stat in batch)
                result[stat.Term] = stat.DocumentFrequency;
        }

        return result;
    }

    /// <summary>
    /// Loads only the documents carrying at least one query term, with just those terms' frequencies.
    /// BM25 scores a document purely from its query-term postings, so documents excluded here would have
    /// scored zero and been discarded anyway — the ranking is identical, the data loaded is orders of
    /// magnitude smaller (11.7M postings for the full test-case corpus versus ~200k for a real query).
    ///
    /// Postings are fetched on the Term index alone and matched to the kind in memory: joining to
    /// IndexedDocuments in SQL measured 4.4 s against 1.7 s for the unjoined read on the production index.
    /// </summary>
    private static async Task<List<SnapshotDocument>> LoadTermScopedDocumentsAsync(
        ImpactIndexDbContext ctx, IndexKind kind, string[] distinct, KindCache kindCache, CancellationToken ct)
    {
        var rows = new List<TermRow>();

        // Chunked so the parameter list can never approach SQLite's variable ceiling, whatever the extractor emits.
        foreach (string[] chunk in Chunk(distinct, TermsPerQuery))
        {
            List<TermRow> batch = await ctx.DocumentTerms
                .Where(t => chunk.Contains(t.Term))
                .Select(t => new TermRow(t.DocumentId, t.Term, t.TermFrequency))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            rows.AddRange(batch);
        }

        List<IGrouping<string, TermRow>> byDoc = rows
            .Where(r => kindCache.DocsById.ContainsKey(r.DocumentId))
            .GroupBy(r => r.DocumentId)
            .ToList();

        Dictionary<string, float[]> vectors = await LoadVectorsAsync(
            ctx, byDoc.Select(g => g.Key).ToArray(), ct).ConfigureAwait(false);

        return byDoc
            .Select(g =>
            {
                DocRow doc = kindCache.DocsById[g.Key];
                return new SnapshotDocument(
                    doc.WorkItemId,
                    kind,
                    doc.Length,
                    doc.ChildCount,
                    g.ToDictionary(x => x.Term, x => x.TermFrequency, StringComparer.Ordinal),
                    vectors.GetValueOrDefault(g.Key));
            })
            .ToList();
    }

    private static async Task<List<SnapshotDocument>> LoadAllDocumentsAsync(
        ImpactIndexDbContext ctx, IndexKind kind, CancellationToken ct)
    {
        var docs = await ctx.Documents
            .Where(d => d.Kind == kind)
            .Select(d => new { d.Id, d.WorkItemId, d.Length, d.ChildCount })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Joined on Kind rather than a doc-id list: an IN clause of one parameter per document exceeds
        // SQLite's variable ceiling on any real corpus.
        Dictionary<string, IReadOnlyDictionary<string, int>> termsByDoc = (await (
                from t in ctx.DocumentTerms
                join d in ctx.Documents on t.DocumentId equals d.Id
                where d.Kind == kind
                select new { t.DocumentId, t.Term, t.TermFrequency })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(t => t.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g.ToDictionary(x => x.Term, x => x.TermFrequency, StringComparer.Ordinal));

        Dictionary<string, float[]> vectorsByDoc = (await (
                from v in ctx.DocumentVectors
                join d in ctx.Documents on v.DocumentId equals d.Id
                where d.Kind == kind
                select new { v.DocumentId, v.Vector })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(v => v.DocumentId, v => VectorBlob.ToFloats(v.Vector));

        var empty = (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();
        return docs
            .Select(d => new SnapshotDocument(
                d.WorkItemId, kind, d.Length, d.ChildCount,
                termsByDoc.GetValueOrDefault(d.Id) ?? empty,
                vectorsByDoc.GetValueOrDefault(d.Id)))
            .ToList();
    }

    private static async Task<Dictionary<string, float[]>> LoadVectorsAsync(
        ImpactIndexDbContext ctx, string[] documentIds, CancellationToken ct)
    {
        // Skipped outright when nothing is embedded, which is the case whenever EmbeddingModel is "none".
        if (documentIds.Length == 0 || !await ctx.DocumentVectors.AnyAsync(ct).ConfigureAwait(false))
            return [];

        var result = new Dictionary<string, float[]>();
        foreach (string[] chunk in Chunk(documentIds, IdsPerQuery))
        {
            List<(string Id, byte[] Vector)> batch = await ctx.DocumentVectors
                .Where(v => chunk.Contains(v.DocumentId))
                .Select(v => ValueTuple.Create(v.DocumentId, v.Vector))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach ((string id, byte[] vector) in batch)
                result[id] = VectorBlob.ToFloats(vector);
        }

        return result;
    }

    /// <summary>Median over the whole kind, matching <c>IndexSnapshot</c>'s upper-middle definition.</summary>
    private static async Task<int> MedianChildCountAsync(ImpactIndexDbContext ctx, IndexKind kind, CancellationToken ct)
    {
        if (kind != IndexKind.Feature)
            return 0;

        int count = await ctx.Documents.CountAsync(d => d.Kind == kind, ct).ConfigureAwait(false);
        if (count == 0)
            return 0;

        return await ctx.Documents
            .Where(d => d.Kind == kind)
            .OrderBy(d => d.ChildCount)
            .Select(d => d.ChildCount)
            .Skip(count / 2)
            .FirstAsync(ct)
            .ConfigureAwait(false);
    }

    private static IEnumerable<T[]> Chunk<T>(T[] source, int size)
    {
        for (int i = 0; i < source.Length; i += size)
            yield return source[i..Math.Min(i + size, source.Length)];
    }

    private readonly record struct TermRow(string DocumentId, string Term, int TermFrequency);

    private static int ParseInt(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

    private static double ParseDouble(IReadOnlyDictionary<string, string> metadata, string key, double fallback)
        => metadata.TryGetValue(key, out string? value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
}
