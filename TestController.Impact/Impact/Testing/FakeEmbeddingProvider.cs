using System.Text.RegularExpressions;
using TestControllerGrpc.Core.Impact.Index;

namespace TestControllerGrpc.Core.Impact.Testing;

/// <summary>
/// Deterministic embedding provider for tests and fixtures (P26): hashes the bag of word tokens into a
/// fixed-length unit vector, so the same text always yields the same vector and texts sharing vocabulary sit
/// close under cosine similarity. No network, fully reproducible.
/// </summary>
public sealed partial class FakeEmbeddingProvider : IEmbeddingProvider
{
    private readonly int _dimension;

    /// <summary>Creates the provider with the given vector dimension.</summary>
    public FakeEmbeddingProvider(int dimension = 16) => _dimension = Math.Max(1, dimension);

    /// <inheritdoc />
    public string ModelId => "fake-hash";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(Vectorize).ToArray());
    }

    /// <summary>Produces the deterministic unit vector for a single text (usable outside the async interface).</summary>
    public float[] Vectorize(string? text)
    {
        var vector = new float[_dimension];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        foreach (string token in WordSplitter().Split(text.ToLowerInvariant()))
        {
            if (token.Length == 0)
            {
                continue;
            }

            vector[Fnv1a(token) % (uint)_dimension] += 1f;
        }

        double norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }

    private static uint Fnv1a(string token)
    {
        uint hash = 2166136261;
        foreach (char c in token)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplitter();
}
