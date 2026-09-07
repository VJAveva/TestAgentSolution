using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Text;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>Summary of one index build/refresh, returned for logging and diagnostics.</summary>
public sealed record IndexBuildResult(
    int DocumentsIndexed, int DocumentsSkipped, int FeaturesSeen, int TestCasesSeen, TimeSpan Elapsed);

/// <summary>
/// Builds and incrementally refreshes the SQLite retrieval index from Azure DevOps (P09). Each Feature and
/// Test Case is flattened to a document, tokenized (shared <see cref="Tokenizer"/>) into BM25 postings, and
/// embedded (<see cref="IEmbeddingProvider"/>). A content fingerprint lets unchanged work items be skipped
/// on refresh, and corpus statistics are recomputed over the ENTIRE corpus so IDF stays correct. The build
/// degrades rather than fails: when embeddings are disabled the index is still fully usable for lexical
/// retrieval. This type is host-agnostic; scheduling lives in <see cref="IndexMaintenanceService"/>.
/// </summary>
public sealed class RetrievalIndexBuilder
{
    private const string WorkItemTypeFeature = "Feature";
    private const string WorkItemTypeTestCase = "Test Case";

    private const string MetaDocumentCount = "DocumentCount";
    private const string MetaAverageLength = "AverageDocumentLength";
    private const string MetaTokenizerVersion = "TokenizerVersion";
    private const string MetaEmbeddingModel = "EmbeddingModel";
    private const string MetaBuiltUtc = "BuiltUtc";
    private const string MetaIndexedThroughUtc = "IndexedThroughUtc";

    private readonly IDbContextFactory<ImpactIndexDbContext> _contextFactory;
    private readonly IAdoWorkItemClient _ado;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ImpactMappingOptions _options;
    private readonly IAppLogger _logger;

    /// <summary>Creates the builder from its collaborators.</summary>
    public RetrievalIndexBuilder(
        IDbContextFactory<ImpactIndexDbContext> contextFactory,
        IAdoWorkItemClient ado,
        IEmbeddingProvider embeddings,
        IOptions<ImpactMappingOptions> options,
        IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _ado = ado ?? throw new ArgumentNullException(nameof(ado));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds or refreshes the index. When <paramref name="fullRebuild"/> is true every work item is
    /// re-fetched; otherwise only items changed since the last recorded watermark are processed.
    /// </summary>
    public async Task<IndexBuildResult> BuildAsync(
        bool fullRebuild, IProgress<ImpactMappingProgress>? progress, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        await using ImpactIndexDbContext ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ImpactIndexInitializer.EnsureCreatedAsync(ctx, ct).ConfigureAwait(false);

        DateTimeOffset since = fullRebuild
            ? DateTimeOffset.MinValue
            : await ReadWatermarkAsync(ctx, ct).ConfigureAwait(false);

        int indexed = 0;
        int skipped = 0;

        Report(progress, "Index:TestCases", 0, 3, "Indexing test cases…");
        IReadOnlyList<AdoWorkItemRef> testCaseRefs = await EnumerateAsync(WorkItemTypeTestCase, since, ct).ConfigureAwait(false);
        foreach (AdoWorkItemRef[] batch in testCaseRefs.Chunk(BatchSize))
        {
            IReadOnlyList<TestCaseCandidate> candidates =
                await _ado.GetTestCasesAsync(batch.Select(r => r.Id).ToArray(), ct).ConfigureAwait(false);
            (int i, int s) = await UpsertAsync(ctx, candidates.Select(DraftFrom).ToArray(), ct).ConfigureAwait(false);
            indexed += i;
            skipped += s;
        }

        Report(progress, "Index:Features", 1, 3, "Indexing features…");
        IReadOnlyList<AdoWorkItemRef> featureRefs = await EnumerateAsync(WorkItemTypeFeature, since, ct).ConfigureAwait(false);
        foreach (AdoWorkItemRef[] batch in featureRefs.Chunk(BatchSize))
        {
            IReadOnlyList<FeatureCandidate> candidates =
                await _ado.GetFeaturesAsync(batch.Select(r => r.Id).ToArray(), ct).ConfigureAwait(false);
            (int i, int s) = await UpsertAsync(ctx, candidates.Select(DraftFrom).ToArray(), ct).ConfigureAwait(false);
            indexed += i;
            skipped += s;
        }

        Report(progress, "Index:Statistics", 2, 3, "Recomputing corpus statistics…");
        await RecomputeCorpusStatisticsAsync(ctx, startedAt, ct).ConfigureAwait(false);

        TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
        _logger.Info("ImpactIndex",
            $"Index build complete: indexed={indexed}, skipped={skipped}, features={featureRefs.Count}, testCases={testCaseRefs.Count}, elapsed={elapsed}.");
        Report(progress, "Index:Done", 3, 3, $"Indexed {indexed}, skipped {skipped}.");

        return new IndexBuildResult(indexed, skipped, featureRefs.Count, testCaseRefs.Count, elapsed);
    }

    private int BatchSize => Math.Max(1, _options.Ado.BatchSize);

    // Small pause between index batches so a background build never starves request handling on the host.
    private const int BatchPauseMilliseconds = 15;

    private async Task<IReadOnlyList<AdoWorkItemRef>> EnumerateAsync(string workItemType, DateTimeOffset since, CancellationToken ct)
    {
        var refs = new List<AdoWorkItemRef>();
        await foreach (AdoWorkItemRef item in _ado
            .EnumerateChangedSinceAsync(workItemType, since, Math.Max(1, _options.Index.IncrementalPageSize), ct)
            .ConfigureAwait(false))
        {
            refs.Add(item);
        }

        // Adaptive date windows can return the same work item more than once; hydrating it twice is wasted
        // ADO traffic and would produce duplicate index rows.
        return refs.DistinctBy(r => r.Id).ToList();
    }

    private static DocumentDraft DraftFrom(TestCaseCandidate testCase)
    {
        string? tags = testCase.Tags is { Count: > 0 } ? string.Join(' ', testCase.Tags) : null;
        string text = ComposeText(testCase.Item.Title, testCase.StepsText, tags);
        return new DocumentDraft($"TC:{testCase.Item.Id}", IndexKind.TestCase, testCase.Item.Id, testCase.Item.Title, text, testCase.Item.Revision, 0);
    }

    private static DocumentDraft DraftFrom(FeatureCandidate feature)
    {
        string text = ComposeText(feature.Item.Title, feature.Description);
        return new DocumentDraft($"F:{feature.Item.Id}", IndexKind.Feature, feature.Item.Id, feature.Item.Title, text, feature.Item.Revision, feature.ChildTestCaseCount);
    }

    private static string ComposeText(params string?[] parts)
        => string.Join('\n', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private async Task<(int indexed, int skipped)> UpsertAsync(
        ImpactIndexDbContext ctx, IReadOnlyList<DocumentDraft> drafts, CancellationToken ct)
    {
        if (drafts.Count == 0)
        {
            return (0, 0);
        }

        // The prior rows are deleted once per batch, so a document repeated inside the batch would insert its
        // terms twice and trip the (DocumentId, Term) unique constraint, aborting the whole build.
        drafts = drafts.GroupBy(d => d.Id, StringComparer.Ordinal).Select(g => g.Last()).ToList();

        string[] ids = drafts.Select(d => d.Id).ToArray();
        Dictionary<string, string> existing = await ctx.Documents
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Fingerprint, ct)
            .ConfigureAwait(false);

        var pending = new List<PendingDocument>();
        int skipped = 0;
        foreach (DocumentDraft draft in drafts)
        {
            string fingerprint = Fingerprint(draft.Text);
            if (existing.TryGetValue(draft.Id, out string? previous) && previous == fingerprint)
            {
                skipped++;
                continue;
            }

            IReadOnlyDictionary<string, int> terms = Tokenizer.TokenizeWithCounts(draft.Text, _options.Keywords);
            pending.Add(new PendingDocument(draft, fingerprint, terms));
        }

        if (pending.Count == 0)
        {
            return (0, skipped);
        }

        IReadOnlyList<float[]> vectors = await EmbedBatchedAsync(pending.Select(p => p.Draft.Text).ToArray(), ct).ConfigureAwait(false);

        // Clear prior rows for the documents being re-indexed before inserting the fresh version.
        string[] reindexIds = pending.Select(p => p.Draft.Id).ToArray();
        await ctx.DocumentTerms.Where(t => reindexIds.Contains(t.DocumentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await ctx.DocumentVectors.Where(v => reindexIds.Contains(v.DocumentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await ctx.Documents.Where(d => reindexIds.Contains(d.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var termRows = new List<(string DocumentId, string Term, int TermFrequency)>();
        for (int i = 0; i < pending.Count; i++)
        {
            PendingDocument item = pending[i];
            int length = item.Terms.Values.Sum();

            ctx.Documents.Add(new IndexedDocument
            {
                Id = item.Draft.Id,
                Kind = item.Draft.Kind,
                WorkItemId = item.Draft.WorkItemId,
                Title = item.Draft.Title,
                Text = item.Draft.Text,
                Fingerprint = item.Fingerprint,
                Revision = item.Draft.Revision,
                Length = length,
                ChildCount = item.Draft.ChildCount,
                UpdatedUtc = now,
            });

            foreach ((string term, int tf) in item.Terms)
                termRows.Add((item.Draft.Id, term, tf));

            float[] vector = i < vectors.Count ? vectors[i] : Array.Empty<float>();
            if (vector.Length > 0)
            {
                ctx.DocumentVectors.Add(new DocumentVector
                {
                    DocumentId = item.Draft.Id,
                    Vector = VectorBlob.FromFloats(vector),
                    Dimension = vector.Length,
                    Model = _embeddings.ModelId,
                });
            }
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        // Term postings outnumber documents ~20:1; insert them with chunked multi-row statements instead of
        // per-row change-tracked entities — turning tens of thousands of INSERTs into a few hundred.
        await BulkInsertTermsAsync(ctx, termRows, ct).ConfigureAwait(false);
        ctx.ChangeTracker.Clear(); // bound memory across batches
        // Yield between batches so a full first-time build never starves request handling on the host.
        await Task.Delay(BatchPauseMilliseconds, ct).ConfigureAwait(false);
        return (pending.Count, skipped);
    }

    // SQLite caps bound variables per statement; 300 rows × 3 params stays under even the legacy 999 limit.
    private static async Task BulkInsertTermsAsync(
        ImpactIndexDbContext ctx, IReadOnlyList<(string DocumentId, string Term, int TermFrequency)> rows, CancellationToken ct)
    {
        const int rowsPerStatement = 300;
        for (int start = 0; start < rows.Count; start += rowsPerStatement)
        {
            int count = Math.Min(rowsPerStatement, rows.Count - start);
            var sql = new StringBuilder("INSERT INTO \"DocumentTerms\" (\"DocumentId\", \"Term\", \"TermFrequency\") VALUES ");
            var args = new object[count * 3];
            for (int r = 0; r < count; r++)
            {
                int p = r * 3;
                if (r > 0) sql.Append(',');
                sql.Append('(').Append('{').Append(p).Append("},{").Append(p + 1).Append("},{").Append(p + 2).Append("})");
                (string documentId, string term, int termFrequency) = rows[start + r];
                args[p] = documentId;
                args[p + 1] = term;
                args[p + 2] = termFrequency;
            }
            await ctx.Database.ExecuteSqlRawAsync(sql.ToString(), args, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!_embeddings.IsEnabled || texts.Count == 0)
        {
            var empty = new float[texts.Count][];
            Array.Fill(empty, Array.Empty<float>());
            return empty;
        }

        var all = new List<float[]>(texts.Count);
        foreach (string[] batch in texts.Chunk(Math.Max(1, _options.Index.EmbeddingBatchSize)))
        {
            all.AddRange(await _embeddings.EmbedAsync(batch, ct).ConfigureAwait(false));
        }

        return all;
    }

    private async Task RecomputeCorpusStatisticsAsync(ImpactIndexDbContext ctx, DateTimeOffset indexedThrough, CancellationToken ct)
    {
        await ctx.CorpusStatistics.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var frequencies = await ctx.DocumentTerms
            .GroupBy(t => t.Term)
            .Select(g => new { Term = g.Key, DocumentFrequency = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        ctx.CorpusStatistics.AddRange(
            frequencies.Select(f => new CorpusStatistic { Term = f.Term, DocumentFrequency = f.DocumentFrequency }));

        int documentCount = await ctx.Documents.CountAsync(ct).ConfigureAwait(false);
        double averageLength = documentCount == 0
            ? 0
            : await ctx.Documents.AverageAsync(d => (double)d.Length, ct).ConfigureAwait(false);

        await UpsertMetaAsync(ctx, MetaDocumentCount, documentCount.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await UpsertMetaAsync(ctx, MetaAverageLength, averageLength.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await UpsertMetaAsync(ctx, MetaTokenizerVersion, Tokenizer.Version, ct).ConfigureAwait(false);
        await UpsertMetaAsync(ctx, MetaEmbeddingModel, _embeddings.ModelId, ct).ConfigureAwait(false);
        await UpsertMetaAsync(ctx, MetaBuiltUtc, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await UpsertMetaAsync(ctx, MetaIndexedThroughUtc, indexedThrough.ToString("O", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertMetaAsync(ImpactIndexDbContext ctx, string key, string value, CancellationToken ct)
    {
        IndexMetadata? row = await ctx.Metadata.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Metadata.Add(new IndexMetadata { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }

    private static async Task<DateTimeOffset> ReadWatermarkAsync(ImpactIndexDbContext ctx, CancellationToken ct)
    {
        IndexMetadata? row = await ctx.Metadata
            .FirstOrDefaultAsync(m => m.Key == MetaIndexedThroughUtc, ct)
            .ConfigureAwait(false);

        return row is not null
            && DateTimeOffset.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset watermark)
            ? watermark
            : DateTimeOffset.MinValue;
    }

    private static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Report(IProgress<ImpactMappingProgress>? progress, string stage, int index, int total, string message)
        => progress?.Report(new ImpactMappingProgress(stage, index, total, message, null));

    private readonly record struct DocumentDraft(
        string Id, IndexKind Kind, int WorkItemId, string Title, string Text, int Revision, int ChildCount);

    private sealed record PendingDocument(DocumentDraft Draft, string Fingerprint, IReadOnlyDictionary<string, int> Terms);
}
