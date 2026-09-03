using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Decorator that memoises embeddings by content hash (P08) so repeated texts — the same change document
/// re-queried, or corpus documents unchanged between rebuilds — are embedded only once. The cache key is
/// model-scoped, so switching <see cref="IEmbeddingProvider.ModelId"/> never returns stale vectors. Empty
/// (failed) vectors are never cached, so a transient outage does not poison later successful calls.
/// </summary>
public sealed class CachingEmbeddingProvider : IEmbeddingProvider
{
    private readonly IEmbeddingProvider _inner;
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    /// <summary>Wraps an inner provider with an in-memory content-addressed cache.</summary>
    public CachingEmbeddingProvider(IEmbeddingProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string ModelId => _inner.ModelId;

    /// <inheritdoc />
    public bool IsEnabled => _inner.IsEnabled;

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var results = new float[inputs.Count][];
        var missingIndices = new List<int>();
        var missingTexts = new List<string>();

        for (int i = 0; i < inputs.Count; i++)
        {
            if (_cache.TryGetValue(CacheKey(inputs[i]), out float[]? cached))
            {
                results[i] = cached;
            }
            else
            {
                missingIndices.Add(i);
                missingTexts.Add(inputs[i]);
            }
        }

        if (missingTexts.Count > 0)
        {
            IReadOnlyList<float[]> embedded = await _inner.EmbedAsync(missingTexts, ct).ConfigureAwait(false);
            for (int j = 0; j < missingIndices.Count; j++)
            {
                float[] vector = j < embedded.Count ? embedded[j] : Array.Empty<float>();
                results[missingIndices[j]] = vector;
                if (vector.Length > 0)
                {
                    _cache[CacheKey(missingTexts[j])] = vector; // only successful vectors are cached
                }
            }
        }

        return results;
    }

    private string CacheKey(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return string.Concat(_inner.ModelId, ":", Convert.ToHexString(hash));
    }
}
