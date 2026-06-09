using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for CachedBuildResultsProvider: cache hits/misses, invalidation,
/// and edge cases.
/// </summary>
public class CachedBuildResultsProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IAppLogger> _mockLogger;

    public CachedBuildResultsProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CacheTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _mockLogger = new Mock<IAppLogger>();
        _mockLogger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<Exception?>()));
        _mockLogger.Setup(l => l.Info(It.IsAny<string>(), It.IsAny<string>()));
        _mockLogger.Setup(l => l.Error(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private CachedBuildResultsProvider CreateProvider(string? rootPath = null)
    {
        var config = new BuildResultsConfig
        {
            ResultsRootPath = rootPath ?? _tempDir,
            GoodThreshold = 95,
            WarningThreshold = 85,
        };
        var parser = new TrxResultsParser();
        var aggregator = new BuildResultsAggregator(config);
        return new CachedBuildResultsProvider(
            parser, aggregator, config, _mockLogger.Object,
            NullLogger<CachedBuildResultsProvider>.Instance);
    }

    private void CreateBuild(string buildNumber, int passed, int failed)
    {
        var buildDir = Path.Combine(_tempDir, buildNumber);
        Directory.CreateDirectory(buildDir);
        var ucDir = Path.Combine(buildDir, "Smoke");
        Directory.CreateDirectory(ucDir);

        var results = new List<string>();
        for (int i = 0; i < passed; i++)
            results.Add($@"<UnitTestResult testName=""Test{i}"" outcome=""Passed"" duration=""00:00:01"" startTime=""{DateTime.UtcNow:o}"" endTime=""{DateTime.UtcNow:o}""/>");
        for (int i = 0; i < failed; i++)
            results.Add($@"<UnitTestResult testName=""FailTest{i}"" outcome=""Failed"" duration=""00:00:01"" startTime=""{DateTime.UtcNow:o}"" endTime=""{DateTime.UtcNow:o}""/>");

        File.WriteAllText(Path.Combine(ucDir, "results.trx"),
            $@"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>{string.Join("", results)}</Results>
</TestRun>");
    }

    [Fact]
    public void GetAllBuilds_Should_ReturnEmpty_When_NoRootPath()
    {
        var provider = CreateProvider(rootPath: "");
        var builds = provider.GetAllBuilds();
        Assert.Empty(builds);
    }

    [Fact]
    public void GetAllBuilds_Should_ReturnEmpty_When_PathDoesNotExist()
    {
        var provider = CreateProvider(Path.Combine(_tempDir, "nonexistent"));
        var builds = provider.GetAllBuilds();
        Assert.Empty(builds);
    }

    [Fact]
    public void GetAllBuilds_Should_ReturnBuilds_When_DataExists()
    {
        CreateBuild("Build1", 10, 0);
        CreateBuild("Build2", 8, 2);

        var provider = CreateProvider();
        var builds = provider.GetAllBuilds();

        Assert.Equal(2, builds.Count);
    }

    [Fact]
    public void GetAllBuilds_Should_CacheResults_When_CalledTwice()
    {
        CreateBuild("Build1", 10, 0);

        var provider = CreateProvider();

        var first = provider.GetAllBuilds();
        var stats1 = provider.GetStats();

        var second = provider.GetAllBuilds();
        var stats2 = provider.GetStats();

        // Both should return same data
        Assert.Equal(first.Count, second.Count);
        // Cache entry count should remain the same
        Assert.Equal(stats1.EntryCount, stats2.EntryCount);
    }

    [Fact]
    public void GetBuild_Should_ReturnNull_When_BuildDoesNotExist()
    {
        var provider = CreateProvider();
        var build = provider.GetBuild("nonexistent");
        Assert.Null(build);
    }

    [Fact]
    public void GetBuild_Should_ReturnBuild_When_Exists()
    {
        CreateBuild("Build1", 10, 0);
        var provider = CreateProvider();

        var build = provider.GetBuild("Build1");
        Assert.NotNull(build);
        Assert.Equal(10, build.TotalTests);
    }

    [Fact]
    public void Invalidate_Should_RemoveCacheEntry_When_BuildExists()
    {
        CreateBuild("Build1", 10, 0);
        var provider = CreateProvider();

        provider.GetAllBuilds(); // Populate cache
        Assert.Equal(1, provider.GetStats().EntryCount);

        provider.Invalidate("Build1");
        Assert.Equal(0, provider.GetStats().EntryCount);
    }

    [Fact]
    public void InvalidateAll_Should_ClearEntireCache_When_Called()
    {
        CreateBuild("Build1", 10, 0);
        CreateBuild("Build2", 8, 2);
        var provider = CreateProvider();

        provider.GetAllBuilds(); // Populate
        Assert.True(provider.GetStats().EntryCount > 0);

        provider.InvalidateAll();
        Assert.Equal(0, provider.GetStats().EntryCount);
    }

    [Fact]
    public void GetStats_Should_ReturnZeroEntries_When_CacheEmpty()
    {
        var provider = CreateProvider();
        var stats = provider.GetStats();

        Assert.Equal(0, stats.EntryCount);
        Assert.Null(stats.OldestEntry);
        Assert.Null(stats.NewestEntry);
    }

    [Fact]
    public void GetStats_Should_ReturnTimestamps_When_CachePopulated()
    {
        CreateBuild("Build1", 10, 0);
        var provider = CreateProvider();
        provider.GetAllBuilds();

        var stats = provider.GetStats();
        Assert.Equal(1, stats.EntryCount);
        Assert.NotNull(stats.OldestEntry);
        Assert.NotNull(stats.NewestEntry);
    }

    [Fact]
    public void CacheStats_Should_HaveCorrectDefaults()
    {
        var stats = new CacheStats(0, null, null);
        Assert.Equal(0, stats.EntryCount);
        Assert.Null(stats.OldestEntry);
        Assert.Null(stats.NewestEntry);
    }
}
