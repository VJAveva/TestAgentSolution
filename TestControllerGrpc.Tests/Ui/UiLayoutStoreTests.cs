using System.IO;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// G-11. Pane sizes persist per display configuration. The load path is the dangerous one: a saved
/// width of 0 renders a pane invisible with no handle to drag it back, and a corrupt file must never
/// be able to stop the app starting. Every one of these asserts a fallback, not a happy path.
///
/// Plain file I/O against a temp directory - no WPF types, no STA thread.
/// </summary>
public sealed class UiLayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uilayout-" + Guid.NewGuid().ToString("N"));

    private UiLayoutStore NewStore() => new(_dir);

    private static UiLayoutSnapshot Valid() => new()
    {
        TreeWidth = 2.5,
        PropertiesWidth = 5,
        AgentWidth = 2,
        LogHeight = 1.5,
        TreePinned = true,
        AgentPinned = false,
        LogPinned = true,
        LogCollapsed = true,
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_Should_ReturnNull_When_NothingSavedYet()
    {
        Assert.Null(NewStore().Load("1920x1080"));
    }

    [Fact]
    public void Load_Should_ReturnSavedLayout_When_DisplayKeyMatches()
    {
        var store = NewStore();
        Assert.True(store.Save("1920x1080", Valid()));

        var loaded = store.Load("1920x1080");

        Assert.NotNull(loaded);
        Assert.Equal(2.5, loaded!.TreeWidth);
        Assert.Equal(5, loaded.PropertiesWidth);
        Assert.False(loaded.AgentPinned);
        Assert.True(loaded.LogCollapsed);
    }

    /// <summary>Docking a laptop must not drag the desktop's layout onto a different screen set.</summary>
    [Fact]
    public void Load_Should_ReturnNull_When_DisplayConfigurationDiffers()
    {
        var store = NewStore();
        store.Save("3840x2160", Valid());

        Assert.Null(store.Load("1920x1080"));
    }

    [Fact]
    public void Save_Should_KeepBothLayouts_When_TwoDisplayConfigurationsUsed()
    {
        var store = NewStore();
        store.Save("3840x2160", Valid());
        store.Save("1920x1080", new UiLayoutSnapshot { TreeWidth = 1, PropertiesWidth = 1, AgentWidth = 1, LogHeight = 1 });

        Assert.Equal(2.5, store.Load("3840x2160")!.TreeWidth);
        Assert.Equal(1, store.Load("1920x1080")!.TreeWidth);
    }

    [Theory]
    [InlineData(0)]               // collapses the pane to nothing
    [InlineData(-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(9999)]            // squeezes every sibling pane to zero
    public void Save_Should_Refuse_When_AWidthWouldMakeAPaneUnusable(double width)
    {
        var store = NewStore();
        var bad = Valid();
        bad.TreeWidth = width;

        Assert.False(store.Save("1920x1080", bad));
        Assert.Null(store.Load("1920x1080"));
    }

    /// <summary>
    /// Defends against a file written by an older build, hand-edited, or truncated by a crash: the app
    /// must fall back to defaults rather than restore a broken layout.
    /// </summary>
    [Fact]
    public void Load_Should_ReturnNull_When_StoredValuesAreOutOfRange()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "ui-layout.json"),
            """{"1920x1080":{"TreeWidth":0,"PropertiesWidth":5,"AgentWidth":2,"LogHeight":1.5}}""");

        Assert.Null(NewStore().Load("1920x1080"));
    }

    [Fact]
    public void Load_Should_ReturnNull_When_FileIsCorrupt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ui-layout.json"), "{ this is not json");

        Assert.Null(NewStore().Load("1920x1080"));
    }

    /// <summary>A corrupt file must not permanently block saving, or the layout can never recover.</summary>
    [Fact]
    public void Save_Should_Succeed_When_ExistingFileIsCorrupt()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "ui-layout.json");
        File.WriteAllText(path, "{ this is not json");

        var store = NewStore();

        Assert.True(store.Save("1920x1080", Valid()));
        Assert.Equal(2.5, store.Load("1920x1080")!.TreeWidth);
    }

    [Fact]
    public void Save_Should_OverwriteInPlace_When_SameDisplayKeySavedTwice()
    {
        var store = NewStore();
        store.Save("1920x1080", Valid());

        var second = Valid();
        second.TreeWidth = 4;
        store.Save("1920x1080", second);

        Assert.Equal(4, store.Load("1920x1080")!.TreeWidth);
    }
}
