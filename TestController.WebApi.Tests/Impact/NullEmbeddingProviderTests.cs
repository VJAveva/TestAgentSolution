using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class NullEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedAsync_Should_ReturnEmptyVector_PerInput()
    {
        IReadOnlyList<float[]> vectors =
            await NullEmbeddingProvider.Instance.EmbedAsync(["a", "b", "c"], CancellationToken.None);

        Assert.Equal(3, vectors.Count);
        Assert.All(vectors, Assert.Empty);
    }

    [Fact]
    public void IsEnabled_Should_BeFalse()
    {
        Assert.False(NullEmbeddingProvider.Instance.IsEnabled);
    }
}
