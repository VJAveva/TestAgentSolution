namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Produces embedding vectors for text (P08). Implementations degrade rather than fail: when embeddings
/// are unavailable the provider returns empty vectors and reports <see cref="IsEnabled"/> = false, letting
/// the retrieval pipeline fall back to lexical-only scoring. Vectors are returned one-per-input, in order.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Identifier of the embedding model; stamped onto stored vectors to prevent mixing models.</summary>
    string ModelId { get; }

    /// <summary>True when the provider actually produces vectors; false for the null/disabled provider.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Embeds a batch of texts, returning one vector per input in the same order. An individual vector may
    /// be empty when embedding is unavailable for that input — callers treat an empty vector as "no
    /// semantic signal" rather than an error.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}
