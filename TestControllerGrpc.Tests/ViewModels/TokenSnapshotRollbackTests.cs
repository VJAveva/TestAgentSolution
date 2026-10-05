using System.IO;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// A parameter file caught mid-save, or saved with a syntax error, must not blank the labels.
/// LoadTokensFromConfig clears every scope before reloading, so without a per-scope rollback one
/// bad keystroke in pipeline-config.json turned the whole tree into "(not set)".
/// </summary>
public class TokenSnapshotRollbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tsr_{Guid.NewGuid():N}");

    public TokenSnapshotRollbackTests()
    {
        TreeNodeViewModel.ClearTokenScopes();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TreeNodeViewModel.ClearTokenScopes();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Snapshot_Should_KeepValuesAndLayers_So_ARollbackRestoresAttribution()
    {
        TreeNodeViewModel.SetToken("Sanity", "_Agent1", "jvgr1", TokenLayer.Profile);

        var snapshot = TreeNodeViewModel.SnapshotTokens();
        TreeNodeViewModel.ClearTokenScopes();
        TreeNodeViewModel.RestoreScopeFrom(snapshot, "Sanity");

        Assert.Equal("jvgr1", TreeNodeViewModel.ResolveTokens("[_Agent1]", "Sanity"));
        // The layer must survive too, or the tooltip silently loses "(Profile)".
        Assert.Contains("(Profile)", TreeNodeViewModel.Describe("[_Agent1]", "Sanity").Describe().Single());
    }

    [Fact]
    public void Rollback_Should_KeepTheLastGoodValues_When_OnePipelineFileIsUnreadable()
    {
        TreeNodeViewModel.SetToken("Sanity", "_Agent1", "jvgr1", TokenLayer.Profile);
        var snapshot = TreeNodeViewModel.SnapshotTokens();

        // Reload begins: every scope is cleared before any file is read.
        TreeNodeViewModel.ClearTokenScopes();
        Assert.Equal("[_Agent1] (not set)", TreeNodeViewModel.ResolveTokens("[_Agent1]", "Sanity"));

        TreeNodeViewModel.RestoreScopeFrom(snapshot, "Sanity");

        Assert.Equal("jvgr1", TreeNodeViewModel.ResolveTokens("[_Agent1]", "Sanity"));
    }

    [Fact]
    public void Rollback_Should_TouchOnlyTheFailingPipeline_So_ABrokenFileDoesNotBlankTheOthers()
    {
        TreeNodeViewModel.SetToken("Sanity", "_Agent1", "jvgr1", TokenLayer.Profile);
        var snapshot = TreeNodeViewModel.SnapshotTokens();

        TreeNodeViewModel.ClearTokenScopes();
        // Warm reloaded fine; only Sanity's file was unreadable.
        TreeNodeViewModel.SetToken("Warm", "_Agent1", "warmgr", TokenLayer.Profile);
        TreeNodeViewModel.RestoreScopeFrom(snapshot, "Sanity");

        Assert.Equal("jvgr1", TreeNodeViewModel.ResolveTokens("[_Agent1]", "Sanity"));
        Assert.Equal("warmgr", TreeNodeViewModel.ResolveTokens("[_Agent1]", "Warm"));
    }

    [Fact]
    public void Rollback_Should_DropTheScope_When_ItDidNotExistBeforeTheReload()
    {
        var snapshot = TreeNodeViewModel.SnapshotTokens();
        TreeNodeViewModel.SetToken("BrandNew", "_Agent1", "jvgr1", TokenLayer.Profile);

        TreeNodeViewModel.RestoreScopeFrom(snapshot, "BrandNew");

        // Restoring "nothing" must mean nothing, not leave the half-loaded value behind.
        Assert.Equal("[_Agent1] (not set)", TreeNodeViewModel.ResolveTokens("[_Agent1]", "BrandNew"));
    }

    [Theory]
    [InlineData("{ \"global\": { \"_Agent1\": ")]            // truncated mid-save
    [InlineData("not json at all")]
    [InlineData("")]
    public void TryLoadJsonConfig_Should_ReturnFalse_So_TheCallerCanRollBack(string content)
    {
        var path = Path.Combine(_dir, "pipeline-config.json");
        File.WriteAllText(path, content);
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };

        // It does NOT throw - it returns false. Rolling back off an exception would therefore
        // never fire, and a malformed file would blank every label with no warning.
        Assert.False(ParameterResolver.TryLoadJsonConfig(ctx, path, "Sanity", "Sanity"));
    }

    [Fact]
    public void TryLoadJsonConfig_Should_ReturnTrue_When_TheFileIsValid()
    {
        var path = Path.Combine(_dir, "good.json");
        File.WriteAllText(path, "{ \"global\": { \"_Agent1\": \"jvgr1\" } }");
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };

        Assert.True(ParameterResolver.TryLoadJsonConfig(ctx, path, "Sanity", "Sanity"));
        Assert.Equal("jvgr1", ctx.Parameters["_Agent1"]);
    }
}
