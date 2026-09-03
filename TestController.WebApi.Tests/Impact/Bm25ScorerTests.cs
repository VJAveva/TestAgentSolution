using TestControllerGrpc.Core.Impact.Index;

namespace TestController.WebApi.Tests.Impact;

public sealed class Bm25ScorerTests
{
    private static Bm25Scorer ScorerOver(IndexSnapshot snapshot) => new(snapshot, k1: 1.2, b: 0.75);

    [Fact]
    public void Score_Should_ReturnZero_When_NoQueryTermInDocument()
    {
        var scorer = ScorerOver(new IndexSnapshot(100, 10, new Dictionary<string, int> { ["login"] = 5 }));

        double score = scorer.Score(["login"], documentLength: 10,
            new Dictionary<string, int> { ["logout"] = 3 });

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_Should_IncreaseWithTermFrequency()
    {
        var scorer = ScorerOver(new IndexSnapshot(100, 10, new Dictionary<string, int> { ["login"] = 5 }));

        double low = scorer.Score(["login"], 10, new Dictionary<string, int> { ["login"] = 1 });
        double high = scorer.Score(["login"], 10, new Dictionary<string, int> { ["login"] = 5 });

        Assert.True(high > low);
    }

    [Fact]
    public void Score_Should_RewardRarerTerm()
    {
        var scorer = ScorerOver(new IndexSnapshot(1000, 10,
            new Dictionary<string, int> { ["rare"] = 1, ["common"] = 900 }));

        double rare = scorer.Score(["rare"], 10, new Dictionary<string, int> { ["rare"] = 2 });
        double common = scorer.Score(["common"], 10, new Dictionary<string, int> { ["common"] = 2 });

        Assert.True(rare > common);
    }

    [Fact]
    public void Score_Should_PenalizeLongerDocument()
    {
        var scorer = ScorerOver(new IndexSnapshot(100, 10, new Dictionary<string, int> { ["login"] = 5 }));

        double shortDoc = scorer.Score(["login"], 5, new Dictionary<string, int> { ["login"] = 2 });
        double longDoc = scorer.Score(["login"], 50, new Dictionary<string, int> { ["login"] = 2 });

        Assert.True(shortDoc > longDoc);
    }

    [Fact]
    public void Score_Should_CountRepeatedQueryTermOnce()
    {
        var scorer = ScorerOver(new IndexSnapshot(100, 10, new Dictionary<string, int> { ["login"] = 5 }));
        var document = new Dictionary<string, int> { ["login"] = 3 };

        double once = scorer.Score(["login"], 10, document);
        double twice = scorer.Score(["login", "login"], 10, document);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Constructor_Should_Throw_When_BOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(IndexSnapshot.Empty, k1: 1.2, b: 1.5));
    }
}
