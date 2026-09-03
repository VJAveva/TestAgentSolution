namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>One document held in a searchable <see cref="IndexSnapshot"/>: its postings and optional vector.</summary>
public sealed record SnapshotDocument(
    int WorkItemId,
    IndexKind Kind,
    int Length,
    int ChildCount,
    IReadOnlyDictionary<string, int> TermFrequencies,
    float[]? Vector);

/// <summary>A retrieved document with its raw leg score, ordered best-first by the snapshot search methods.</summary>
public readonly record struct RankedDocument(int WorkItemId, double Score);

/// <summary>
/// Immutable, read-optimised in-memory view of the retrieval index (P07 + P13). Holds corpus statistics
/// for IDF (which MUST reflect the ENTIRE corpus, never a retrieved subset) plus the per-document postings
/// and vectors needed to search. Lexical (<see cref="SearchBm25"/>) and dense (<see cref="SearchDense"/>)
/// legs both return work item ids so retrieval stays independent of Azure DevOps hydration. The snapshot is
/// typically built kind-scoped (features or test cases) by the orchestrator so its searches never mix kinds.
/// </summary>
public sealed class IndexSnapshot
{
    private readonly IReadOnlyDictionary<string, int> _documentFrequencies;
    private readonly IReadOnlyList<SnapshotDocument> _documents;
    private readonly IReadOnlyDictionary<int, float[]> _vectorsById;
    private readonly double _bm25K1;
    private readonly double _bm25B;

    /// <summary>Creates a statistics-only snapshot (no searchable documents).</summary>
    public IndexSnapshot(
        int documentCount, double averageDocumentLength, IReadOnlyDictionary<string, int> documentFrequencies)
        : this(documentCount, averageDocumentLength, documentFrequencies, [])
    {
    }

    /// <summary>Creates a searchable snapshot from corpus totals, term frequencies and documents.</summary>
    public IndexSnapshot(
        int documentCount,
        double averageDocumentLength,
        IReadOnlyDictionary<string, int> documentFrequencies,
        IReadOnlyList<SnapshotDocument> documents,
        double bm25K1 = 1.2,
        double bm25B = 0.75)
    {
        ArgumentNullException.ThrowIfNull(documentFrequencies);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentOutOfRangeException.ThrowIfNegative(documentCount);
        ArgumentOutOfRangeException.ThrowIfNegative(averageDocumentLength);

        DocumentCount = documentCount;
        // Guard the BM25 length-normalisation divisor against an empty/degenerate corpus.
        AverageDocumentLength = averageDocumentLength <= 0 ? 1.0 : averageDocumentLength;
        _documentFrequencies = documentFrequencies;
        _documents = documents;
        _bm25K1 = bm25K1;
        _bm25B = bm25B;
        MedianChildCount = ComputeMedianChildCount(documents);

        var vectors = new Dictionary<int, float[]>();
        foreach (SnapshotDocument document in documents)
        {
            if (document.Vector is { Length: > 0 })
            {
                vectors[document.WorkItemId] = document.Vector;
            }
        }

        _vectorsById = vectors;
    }

    /// <summary>Total number of documents in the corpus (N).</summary>
    public int DocumentCount { get; }

    /// <summary>Mean document length in tokens (avgdl), never zero.</summary>
    public double AverageDocumentLength { get; }

    /// <summary>Median child-test-case count across feature documents (drives the P17 fan-out penalty).</summary>
    public int MedianChildCount { get; }

    /// <summary>Corpus document frequency for a term (0 when unknown).</summary>
    public int DocumentFrequency(string term)
        => _documentFrequencies.TryGetValue(term, out int df) ? df : 0;

    /// <summary>The dense vector for a document, or null when it was not embedded (used for MMR diversity).</summary>
    public float[]? VectorFor(int workItemId)
        => _vectorsById.TryGetValue(workItemId, out float[]? vector) ? vector : null;

    /// <summary>
    /// Lucene-style BM25 inverse document frequency: <c>ln(1 + (N - df + 0.5) / (df + 0.5))</c>.
    /// The <c>1 +</c> keeps the result non-negative even for terms present in every document.
    /// </summary>
    public double InverseDocumentFrequency(string term)
    {
        int df = DocumentFrequency(term);
        return Math.Log(1.0 + (DocumentCount - df + 0.5) / (df + 0.5));
    }

    /// <summary>Scores every document by BM25 against the query terms, returning the top ids best-first.</summary>
    public IReadOnlyList<RankedDocument> SearchBm25(IReadOnlyCollection<string> terms, int limit)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0 || _documents.Count == 0 || limit <= 0)
        {
            return [];
        }

        var scorer = new Bm25Scorer(this, _bm25K1, _bm25B);
        var scored = new List<RankedDocument>();
        foreach (SnapshotDocument doc in _documents)
        {
            double score = scorer.Score(terms, doc.Length, doc.TermFrequencies);
            if (score > 0)
            {
                scored.Add(new RankedDocument(doc.WorkItemId, score));
            }
        }

        return TopByScore(scored, limit);
    }

    /// <summary>Ranks documents by cosine similarity to the dense query, returning the top ids best-first.</summary>
    public IReadOnlyList<RankedDocument> SearchDense(float[] query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0 || _documents.Count == 0 || limit <= 0)
        {
            return [];
        }

        var scored = new List<RankedDocument>();
        foreach (SnapshotDocument doc in _documents)
        {
            if (doc.Vector is null || doc.Vector.Length != query.Length)
            {
                continue;
            }

            scored.Add(new RankedDocument(doc.WorkItemId, CosineSimilarity(query, doc.Vector)));
        }

        return TopByScore(scored, limit);
    }

    /// <summary>An empty corpus snapshot (no documents, no statistics).</summary>
    public static IndexSnapshot Empty { get; } = new(0, 1.0, new Dictionary<string, int>());

    private static IReadOnlyList<RankedDocument> TopByScore(List<RankedDocument> scored, int limit)
        => scored
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.WorkItemId) // deterministic tie-break
            .Take(limit)
            .ToList();

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static int ComputeMedianChildCount(IReadOnlyList<SnapshotDocument> documents)
    {
        int[] counts = documents
            .Where(d => d.Kind == IndexKind.Feature)
            .Select(d => d.ChildCount)
            .OrderBy(c => c)
            .ToArray();

        return counts.Length == 0 ? 0 : counts[counts.Length / 2];
    }
}
