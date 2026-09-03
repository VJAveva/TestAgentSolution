namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// The degradation embedding provider (P08): returns an empty vector for every input and reports
/// <see cref="IsEnabled"/> = false. Registered whenever no embedding endpoint is configured so the
/// engine runs lexical-only without any special-casing at the call sites.
/// </summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Shared stateless instance.</summary>
    public static NullEmbeddingProvider Instance { get; } = new();

    /// <inheritdoc />
    public string ModelId => "none";

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var vectors = new float[inputs.Count][];
        Array.Fill(vectors, Array.Empty<float>());
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }
}
