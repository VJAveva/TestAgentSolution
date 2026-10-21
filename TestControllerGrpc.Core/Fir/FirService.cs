using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Fir;

/// <summary>The packs loaded once at startup, so every report and every cache key sees the same set.</summary>
public sealed record FirRulePackSet(IReadOnlyList<FirRulePack> Packs, IReadOnlyList<string> Errors)
{
    public static FirRulePackSet Empty { get; } = new([], []);

    public IReadOnlyList<string> Labels => [.. Packs.Select(p => p.Label)];
}

/// <summary>
/// Produces a report for one test, cached as &lt;Test&gt;.fir.json beside the TRX. Reports are computed
/// on demand rather than at collection time, because every older build already on disk has none.
/// </summary>
public sealed class FirService
{
    internal const string CacheSuffix = ".fir.json";

    /// <summary>Shared so an endpoint returns byte-for-byte what the cache file holds.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TrxResultsParser _parser;
    private readonly FirAnalyzer _analyzer;
    private readonly BuildResultsConfig _config;
    private readonly FirRulePackSet _packs;
    private readonly IAppLogger _logger;
    private readonly FirOptions _options;

    public FirService(
        TrxResultsParser parser,
        FirAnalyzer analyzer,
        BuildResultsConfig config,
        FirRulePackSet packs,
        IAppLogger logger,
        FirOptions? options = null)
    {
        _parser = parser;
        _analyzer = analyzer;
        _config = config;
        _packs = packs;
        _logger = logger;
        _options = options ?? FirOptions.Default;
    }

    /// <summary>Null when the test is not in that build, or it did not fail.</summary>
    public FirReport? GetOrCompute(string buildNumber, string testName, bool refresh = false)
    {
        if (string.IsNullOrWhiteSpace(buildNumber) || string.IsNullOrWhiteSpace(testName)) return null;
        if (Locate(buildNumber, testName) is not { } found) return null;

        var logPath = LogBeside(found.Folder, testName);
        var cachePath = Path.Combine(found.Folder, SafeFileName(testName) + CacheSuffix);

        if (!refresh && ReadCache(cachePath) is { } cached && IsCurrent(cached, found.TrxPath, logPath, found.Folder))
            return cached;

        var report = Compute(found, logPath);
        if (report is null) return null;

        WriteCache(cachePath, report);
        return report;
    }

    /// <summary>Fills a whole build's reports in one pass, for results that pre-date this feature.</summary>
    public int Backfill(string buildNumber, bool refresh = false)
    {
        var written = 0;
        foreach (var test in FailedTestsIn(buildNumber))
            if (GetOrCompute(buildNumber, test, refresh) is not null)
                written++;

        return written;
    }

    private IEnumerable<string> FailedTestsIn(string buildNumber)
    {
        var folder = BuildFolder(buildNumber);
        if (folder is null) return [];

        try
        {
            return [.. _parser.ParseBuildFolder(folder).AllFailedTests.Select(t => t.TestName).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Fir", $"Could not enumerate build '{buildNumber}': {ex.Message}");
            return [];
        }
    }

    private FirReport? Compute((TestResult Test, UseCaseNode UseCase, string Folder, string TrxPath) found, string? logPath)
    {
        var input = FirTrxInput.FromTestResult(found.Test, found.UseCase.Agent, ReadLog(logPath)) with
        {
            ResultsFolder = found.Folder,
        };

        var report = _analyzer.Analyze(input);
        return report is null
            ? null
            : report with
            {
                CacheKey = FirCacheKey.For(
                    found.TrxPath, logPath, CurrentWorkbook(report.DataWorkbook, found.Folder), _packs.Labels),
            };
    }

    private bool IsCurrent(FirReport cached, string trxPath, string? logPath, string resultsFolder)
        => cached.CacheKey is { Length: > 0 }
           && cached.CacheKey == FirCacheKey.For(
               trxPath, logPath, CurrentWorkbook(cached.DataWorkbook, resultsFolder), _packs.Labels);

    /// <summary>
    /// Re-resolved from the logged path on BOTH the compute and the check path, so a workbook that only
    /// becomes resolvable later still invalidates the cache. The report's own ResolvedWorkbook cannot be
    /// used for this: it is null both when no workbook was found and when one was found but failed to read,
    /// so the two sides would disagree and the cache would never hit.
    /// </summary>
    private string? CurrentWorkbook(string? loggedPath, string resultsFolder)
        => FirWorkbookResolver.Resolve(loggedPath, resultsFolder, _options)?.Path;

    private (TestResult Test, UseCaseNode UseCase, string Folder, string TrxPath)? Locate(string buildNumber, string testName)
    {
        var buildFolder = BuildFolder(buildNumber);
        if (buildFolder is null) return null;

        BuildNode build;
        try
        {
            build = _parser.ParseBuildFolder(buildFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Fir", $"Could not read build '{buildNumber}': {ex.Message}");
            return null;
        }

        foreach (var useCase in build.UseCases)
        {
            var test = useCase.TestResults
                .FirstOrDefault(t => string.Equals(t.TestName, testName, StringComparison.OrdinalIgnoreCase));
            if (test is null) continue;

            // Flat builds have no use-case folder; the parser still groups them by feature name.
            var folder = Path.Combine(buildFolder, useCase.UseCaseName);
            if (!Directory.Exists(folder)) folder = buildFolder;

            return (test, useCase, folder, Path.Combine(folder, test.TrxFileName + ".trx"));
        }

        return null;
    }

    private string? BuildFolder(string buildNumber)
    {
        var root = _config.ResultsRootPath;
        if (string.IsNullOrWhiteSpace(root)) return null;

        try
        {
            return Path.Combine(root, SafeFileName(buildNumber));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The vstest console log, when the run captured one beside its TRX.</summary>
    private static string? LogBeside(string folder, string testName)
    {
        var path = Path.Combine(folder, SafeFileName(testName) + ".log");
        return File.Exists(path) ? path : null;
    }

    private string? ReadLog(string? path)
    {
        if (path is null) return null;

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Fir", $"Could not read '{path}': {ex.Message}");
            return null;
        }
    }

    private FirReport? ReadCache(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<FirReport>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or half-written cache is not worth a failure; recomputing is cheap.
            return null;
        }
    }

    /// <summary>The results root is usually a share, so a failed write must never fail the report.</summary>
    private void WriteCache(string path, FirReport report)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.Log(LogLevel.Debug, "Fir", $"Could not cache '{path}': {ex.Message}");
        }
    }

    internal static string SafeFileName(string name)
        => string.Concat(name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
}
