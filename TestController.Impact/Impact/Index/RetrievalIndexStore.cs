using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>Loads kind-scoped searchable <see cref="IndexSnapshot"/> instances from the persisted index (P24).</summary>
public interface IRetrievalIndexStore
{
    /// <summary>Materialises an in-memory snapshot of the documents of one kind, with corpus-wide statistics.</summary>
    Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, CancellationToken ct);
}

/// <summary>
/// Bridges the persisted SQLite index to the in-memory <see cref="IndexSnapshot"/> the retriever searches
/// (P24). Documents are loaded kind-scoped (features or test cases) so searches never mix kinds, but corpus
/// statistics (document count, average length and term document frequencies) are loaded corpus-wide so IDF
/// stays a true global rarity measure.
/// </summary>
public sealed class RetrievalIndexStore : IRetrievalIndexStore
{
    private readonly IDbContextFactory<ImpactIndexDbContext> _contextFactory;
    private readonly ImpactMappingOptions.RetrievalOptions _retrieval;
    private readonly IImpactIndexHealthCheck? _health;

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
    public async Task<IndexSnapshot> GetSnapshotAsync(IndexKind kind, CancellationToken ct)
    {
        if (_health is not null)
        {
            ImpactIndexHealth health = await _health.CheckAsync(ct).ConfigureAwait(false);
            if (health.IsUnusable)
                throw new ImpactIndexUnavailableException(health);
        }

        await using ImpactIndexDbContext ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ImpactIndexInitializer.EnsureCreatedAsync(ctx, ct).ConfigureAwait(false);

        Dictionary<string, string> metadata = await ctx.Metadata
            .ToDictionaryAsync(m => m.Key, m => m.Value, ct)
            .ConfigureAwait(false);
        int corpusDocumentCount = ParseInt(metadata, "DocumentCount");
        double averageLength = ParseDouble(metadata, "AverageDocumentLength", 1.0);

        Dictionary<string, int> documentFrequencies = await ctx.CorpusStatistics
            .ToDictionaryAsync(c => c.Term, c => c.DocumentFrequency, ct)
            .ConfigureAwait(false);

        var docs = await ctx.Documents
            .Where(d => d.Kind == kind)
            .Select(d => new { d.Id, d.WorkItemId, d.Length, d.ChildCount })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        List<string> docIds = docs.Select(d => d.Id).ToList();

        Dictionary<string, IReadOnlyDictionary<string, int>> termsByDoc = (await ctx.DocumentTerms
                .Where(t => docIds.Contains(t.DocumentId))
                .Select(t => new { t.DocumentId, t.Term, t.TermFrequency })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(t => t.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g.ToDictionary(x => x.Term, x => x.TermFrequency, StringComparer.Ordinal));

        Dictionary<string, float[]> vectorsByDoc = (await ctx.DocumentVectors
                .Where(v => docIds.Contains(v.DocumentId))
                .Select(v => new { v.DocumentId, v.Vector })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(v => v.DocumentId, v => VectorBlob.ToFloats(v.Vector));

        var empty = (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();
        List<SnapshotDocument> snapshotDocs = docs
            .Select(d => new SnapshotDocument(
                d.WorkItemId, kind, d.Length, d.ChildCount,
                termsByDoc.GetValueOrDefault(d.Id) ?? empty,
                vectorsByDoc.GetValueOrDefault(d.Id)))
            .ToList();

        int documentCount = corpusDocumentCount > 0 ? corpusDocumentCount : snapshotDocs.Count;
        return new IndexSnapshot(documentCount, averageLength, documentFrequencies, snapshotDocs, _retrieval.Bm25K1, _retrieval.Bm25B);
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

    private static double ParseDouble(IReadOnlyDictionary<string, string> metadata, string key, double fallback)
        => metadata.TryGetValue(key, out string? value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
}
