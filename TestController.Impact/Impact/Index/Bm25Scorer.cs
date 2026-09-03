namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Computes Okapi BM25 relevance of a document to a set of query terms (P07). Pure and deterministic:
/// for the same snapshot, parameters and document it always yields the same score. Corpus-level IDF
/// comes from the <see cref="IndexSnapshot"/>; per-document term frequencies and length are supplied by
/// the caller, so the scorer never touches the database and is trivially unit-testable.
/// </summary>
public sealed class Bm25Scorer
{
    private readonly IndexSnapshot _snapshot;
    private readonly double _k1;
    private readonly double _b;

    /// <summary>Creates a scorer over a corpus snapshot with the given BM25 parameters.</summary>
    /// <param name="snapshot">Corpus statistics providing IDF and average document length.</param>
    /// <param name="k1">Term-frequency saturation (typically ~1.2); must be non-negative.</param>
    /// <param name="b">Length-normalisation strength in [0, 1] (typically 0.75).</param>
    public Bm25Scorer(IndexSnapshot snapshot, double k1 = 1.2, double b = 0.75)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(k1);
        if (b is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(b), b, "BM25 b must be within [0, 1].");
        }

        _snapshot = snapshot;
        _k1 = k1;
        _b = b;
    }

    /// <summary>
    /// Scores a document against the given query terms. <paramref name="termFrequencies"/> maps a term to
    /// its frequency within the document and <paramref name="documentLength"/> is the document length in
    /// tokens. Each distinct query term contributes at most once; terms absent from the document add
    /// nothing, so a document sharing no terms with the query scores exactly 0.
    /// </summary>
    public double Score(
        IEnumerable<string> queryTerms,
        int documentLength,
        IReadOnlyDictionary<string, int> termFrequencies)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(termFrequencies);

        double lengthNorm = _k1 * (1.0 - _b + _b * (documentLength / _snapshot.AverageDocumentLength));

        double score = 0.0;
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string term in queryTerms)
        {
            if (!counted.Add(term))
            {
                continue; // a repeated query term is scored once
            }

            if (!termFrequencies.TryGetValue(term, out int tf) || tf <= 0)
            {
                continue;
            }

            double idf = _snapshot.InverseDocumentFrequency(term);
            score += idf * (tf * (_k1 + 1.0)) / (tf + lengthNorm);
        }

        return score;
    }
}
