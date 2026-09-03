using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class CachingEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedAsync_Should_ReturnVector_PerInput_InOrder()
    {
        var inner = new RecordingEmbeddingProvider(text => new float[] { text.Length });
        var caching = new CachingEmbeddingProvider(inner);

        IReadOnlyList<float[]> vectors = await caching.EmbedAsync(["a", "bb", "ccc"], CancellationToken.None);

        Assert.Equal(3, vectors.Count);
        Assert.Equal(new float[] { 1f }, vectors[0]);
        Assert.Equal(new float[] { 2f }, vectors[1]);
        Assert.Equal(new float[] { 3f }, vectors[2]);
    }

    [Fact]
    public async Task EmbedAsync_Should_NotReEmbedCachedText_OnSecondCall()
    {
        var inner = new RecordingEmbeddingProvider(text => new float[] { text.Length });
        var caching = new CachingEmbeddingProvider(inner);

        await caching.EmbedAsync(["alpha", "beta"], CancellationToken.None);
        await caching.EmbedAsync(["beta", "gamma"], CancellationToken.None);

        // "beta" is served from cache on the second call, so the inner provider never re-embeds it.
        Assert.Equal(["alpha", "beta", "gamma"], inner.EmbeddedTexts);
    }

    [Fact]
    public async Task EmbedAsync_Should_NotCacheEmptyVectors()
    {
        // "fail" simulates a transient outage returning an empty vector; it must be retried, not cached.
        var inner = new RecordingEmbeddingProvider(
            text => text == "fail" ? Array.Empty<float>() : new float[] { text.Length });
        var caching = new CachingEmbeddingProvider(inner);

        await caching.EmbedAsync(["fail"], CancellationToken.None);
        await caching.EmbedAsync(["fail"], CancellationToken.None);

        Assert.Equal(["fail", "fail"], inner.EmbeddedTexts);
    }

    [Fact]
    public void Properties_Should_PassThroughToInner()
    {
        var caching = new CachingEmbeddingProvider(new RecordingEmbeddingProvider(text => new float[] { text.Length }));

        Assert.Equal("fake-model", caching.ModelId);
        Assert.True(caching.IsEnabled);
    }

    private sealed class RecordingEmbeddingProvider(Func<string, float[]> embed) : IEmbeddingProvider
    {
        private readonly Func<string, float[]> _embed = embed;

        public List<string> EmbeddedTexts { get; } = [];

        public string ModelId => "fake-model";

        public bool IsEnabled => true;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            EmbeddedTexts.AddRange(inputs);
            IReadOnlyList<float[]> result = inputs.Select(_embed).ToArray();
            return Task.FromResult(result);
        }
    }
}
