using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class IndexSnapshotTests
{
    [Fact]
    public void InverseDocumentFrequency_Should_BeHigher_ForRarerTerm()
    {
        var snapshot = new IndexSnapshot(1000, 10, new Dictionary<string, int>
        {
            ["rare"] = 1,
            ["common"] = 800,
        });

        Assert.True(snapshot.InverseDocumentFrequency("rare") > snapshot.InverseDocumentFrequency("common"));
    }

    [Fact]
    public void InverseDocumentFrequency_Should_BeNonNegative_ForUbiquitousTerm()
    {
        var snapshot = new IndexSnapshot(100, 10, new Dictionary<string, int> { ["everywhere"] = 100 });

        Assert.True(snapshot.InverseDocumentFrequency("everywhere") >= 0);
    }

    [Fact]
    public void DocumentFrequency_Should_ReturnZero_ForUnknownTerm()
    {
        var snapshot = new IndexSnapshot(10, 5, new Dictionary<string, int>());

        Assert.Equal(0, snapshot.DocumentFrequency("missing"));
    }

    [Fact]
    public void Constructor_Should_GuardAverageLength_When_CorpusEmpty()
    {
        var snapshot = new IndexSnapshot(0, 0, new Dictionary<string, int>());

        Assert.Equal(1.0, snapshot.AverageDocumentLength);
    }
}
