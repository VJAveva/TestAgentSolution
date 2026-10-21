using TestControllerGrpc.Core.Fir;

namespace TestControllerGrpc.Tests.Fir;

/// <summary>
/// A cached report is only valid while every input that shaped it is unchanged - including the rule
/// packs, which change a verdict without touching any result file.
/// </summary>
public class FirCacheKeyTests
{
    private static string Key(string trx, string? log = null, string? book = null, params string[] packs)
        => FirCacheKey.For(trx, log, book, packs.Length == 0 ? ["generic"] : packs);

    [Fact]
    public void For_Should_BeStable_When_NothingChanged()
    {
        using var dir = new TempDirectory("FirKey");
        var trx = dir.CreateFile("t.trx", "x");

        Assert.Equal(Key(trx), Key(trx));
    }

    [Fact]
    public void For_Should_Change_When_TheTrxIsRewritten()
    {
        using var dir = new TempDirectory("FirKey");
        var trx = dir.CreateFile("t.trx", "x");
        var before = Key(trx);

        File.WriteAllText(trx, "a longer result file");

        Assert.NotEqual(before, Key(trx));
    }

    [Fact]
    public void For_Should_Change_When_TheWorkbookIsReplaced()
    {
        using var dir = new TempDirectory("FirKey");
        var trx = dir.CreateFile("t.trx", "x");
        var book = dir.CreateFile("TestData/WASSet1.xlsx", "v1");
        var before = Key(trx, book: book);

        File.WriteAllText(book, "v2 is longer");

        Assert.NotEqual(before, Key(trx, book: book));
    }

    [Fact]
    public void For_Should_Change_When_ARulePackVersionMoves()
    {
        using var dir = new TempDirectory("FirKey");
        var trx = dir.CreateFile("t.trx", "x");

        Assert.NotEqual(
            Key(trx, packs: ["generic", "WASSmokeTest@1"]),
            Key(trx, packs: ["generic", "WASSmokeTest@2"]));
    }

    [Fact]
    public void For_Should_Change_When_TheLogArrivesLater()
    {
        using var dir = new TempDirectory("FirKey");
        var trx = dir.CreateFile("t.trx", "x");
        var before = Key(trx);

        var log = dir.CreateFile("t.log", "lines");

        Assert.NotEqual(before, Key(trx, log));
    }

    [Fact]
    public void For_Should_NotThrow_When_APathIsMissingOrInvalid()
    {
        var key = FirCacheKey.For("Z:\\no\\such.trx", null, "", ["generic"]);

        Assert.False(string.IsNullOrWhiteSpace(key));
    }
}
