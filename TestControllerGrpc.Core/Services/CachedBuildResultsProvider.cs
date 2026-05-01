using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Caches parsed TRX build results keyed by build folder path and last-write-time.
/// Since build results are immutable after creation, the cache hit rate approaches
/// 100% for historical builds. Only the latest (actively-writing) build folder
/// will ever need re-parsing.
///
/// Thread-safe via ConcurrentDictionary. No background timer — cache is populated
/// lazily on first request and invalidated only when folder modification time changes.
///
/// Used by ResultsController.GetBuilds() to reduce latency from O(N×M) TRX parsing
/// per request to O(1) dictionary lookups.
/// </summary>
public sealed class CachedBuildResultsProvider
{
    private readonly TrxResultsParser _parser;
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;
    private readonly ILogger<CachedBuildResultsProvider> _logger;
    private readonly IAppLogger _appLogger;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private record CacheEntry(
        DateTime FolderModified,
        BuildNode Node,
        DateTime CachedAtUtc);

    public CachedBuildResultsProvider(
        TrxResultsParser parser,
        BuildResultsAggregator aggregator,
        BuildResultsConfig config,
        IAppLogger appLogger,
        ILogger<CachedBuildResultsProvider> logger)
    {
        _parser = parser;
        _aggregator = aggregator;
        _config = config;
        _appLogger = appLogger;
        _logger = logger;
    }

    /// <summary>
    /// Returns all builds with cached parsed results.
    /// Only parses TRX files for builds not yet in cache or whose folder has been modified.
    /// </summary>
    public IReadOnlyList<BuildNode> GetAllBuilds()
    {
        var rootPath = _config.ResultsRootPath;
        if (string.IsNullOrEmpty(rootPath))
        {
            _logger.LogWarning(
                "BuildResults: ResultsRootPath is not configured. " +
                "Set BuildResults:ResultsRootPath in appsettings to enable the Results dashboard.");
            _appLogger.Log(LogLevel.Warning, "BuildResults",
                "ResultsRootPath is not configured ? Results dashboard will be empty.");
            return [];
        }
        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning(
                "BuildResults: ResultsRootPath '{Path}' does not exist or is not accessible. " +
                "Verify the deployment environment has read access to this folder.",
                rootPath);
            _appLogger.Log(LogLevel.Warning, "BuildResults",
                $"ResultsRootPath '{rootPath}' not found or not accessible ? Results dashboard will be empty.");
            return [];
        }

        var buildFolders = _parser.DiscoverBuilds(rootPath);
        var results = new List<BuildNode>(buildFolders.Count);
        int cacheMisses = 0;

        foreach (var (buildNumber, path, modified) in buildFolders)
        {
            var (node, wasCached) = GetOrParse(path);
            if (node != null)
                results.Add(node);
            if (!wasCached)
                cacheMisses++;
        }

        if (cacheMisses > 0)
        {
            _logger.LogInformation(
                "BuildResults cache: {Total} builds, {Hits} cached, {Misses} parsed",
                results.Count, results.Count - cacheMisses, cacheMisses);
        }

        return results;
    }

    /// <summary>
    /// Returns a single build's parsed results, from cache if available.
    /// </summary>
    public BuildNode? GetBuild(string buildNumber)
    {
        var rootPath = _config.ResultsRootPath;
        if (string.IsNullOrEmpty(rootPath)) return null;

        var folder = Path.Combine(rootPath, buildNumber);
        if (!Directory.Exists(folder)) return null;

        return GetOrParse(folder).Node;
    }

    /// <summary>
    /// Forces re-parse of a specific build (e.g., after new TRX files are added).
    /// </summary>
    public void Invalidate(string buildNumber)
    {
        var rootPath = _config.ResultsRootPath;
        if (string.IsNullOrEmpty(rootPath)) return;

        var folder = Path.Combine(rootPath, buildNumber);
        _cache.TryRemove(folder, out _);
        _logger.LogInformation("BuildResults cache invalidated: {Build}", buildNumber);
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void InvalidateAll()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("BuildResults cache cleared: {Count} entries removed", count);
    }

    /// <summary>
    /// Returns cache statistics for diagnostics.
    /// </summary>
    public CacheStats GetStats() => new(
        EntryCount: _cache.Count,
        OldestEntry: _cache.Values.MinBy(e => e.CachedAtUtc)?.CachedAtUtc,
        NewestEntry: _cache.Values.MaxBy(e => e.CachedAtUtc)?.CachedAtUtc);

    private (BuildNode? Node, bool WasCached) GetOrParse(string folderPath)
    {
        var folderName = Path.GetFileName(folderPath);
        var folderModified = Directory.GetLastWriteTimeUtc(folderPath);

        // Cache hit: folder hasn't changed since last parse
        if (_cache.TryGetValue(folderPath, out var cached) &&
            cached.FolderModified == folderModified)
        {
            return (cached.Node, true);
        }

        // Cache miss: parse TRX files
        _appLogger.Info("BuildResultsCache", $"Cache MISS for {folderName} — parsing TRX files");
        try
        {
            var node = _parser.ParseBuildFolder(folderPath);
            node = _aggregator.EvaluateBuildHealth(node);

            _appLogger.Info("BuildResultsCache",
                $"Parsed {folderName}: {node.TotalTests} tests, {node.PassRate:F1}% pass, health={node.Health}");

            _cache[folderPath] = new CacheEntry(folderModified, node, DateTime.UtcNow);
            return (node, false);
        }
        catch (Exception ex)
        {
            _appLogger.Error("BuildResultsCache", $"FAILED to parse {folderName}: {ex.Message}", ex);
            _logger.LogWarning(ex, "Failed to parse build folder: {Folder}", folderName);
            return (null, false);
        }
    }
}

public record CacheStats(int EntryCount, DateTime? OldestEntry, DateTime? NewestEntry);
