using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class VectorBlobTests
{
    [Fact]
    public void FromFloats_Should_RoundtripValues_When_VectorNonEmpty()
    {
        float[] original = [0f, 1.5f, -2.25f, float.MaxValue, float.MinValue, 3.14159f];

        byte[] blob = VectorBlob.FromFloats(original);
        float[] roundtripped = VectorBlob.ToFloats(blob);

        Assert.Equal(original.Length * sizeof(float), blob.Length);
        Assert.Equal(original, roundtripped);
    }

    [Fact]
    public void FromFloats_Should_ReturnEmpty_When_InputEmpty()
    {
        Assert.Empty(VectorBlob.FromFloats(ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void ToFloats_Should_ReturnEmpty_When_BlobEmpty()
    {
        Assert.Empty(VectorBlob.ToFloats(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ToFloats_Should_Throw_When_LengthNotMultipleOfFour()
    {
        Assert.Throws<ArgumentException>(() => VectorBlob.ToFloats(new byte[] { 1, 2, 3 }));
    }
}
