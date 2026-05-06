using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

public class BuildTrendAnalyzer
{
    private readonly BuildResultsAggregator _aggregator;
    private readonly BuildResultsConfig _config;

    public BuildTrendAnalyzer(BuildResultsAggregator aggregator, BuildResultsConfig config)
    {
        _aggregator = aggregator;
        _config = config;
    }

    public TrendReport AnalyzeTrends(string resultsRootPath, TrxResultsParser parser)
    {
        var builds = parser.DiscoverBuilds(resultsRootPath);
        var buildResults = new List<BuildTrendEntry>();

        foreach (var (buildNumber, path, modified) in builds.Take(50))
        {
            try
            {
                var node = parser.ParseBuildFolder(path);
                node = _aggregator.EvaluateBuildHealth(node);

                buildResults.Add(new BuildTrendEntry
                {
                    BuildNumber = buildNumber,
                    Date = modified,
                    TotalTests = node.TotalTests,
                    PassedTests = node.PassedTests,
                    FailedTests = node.FailedTests,
                    TimeoutTests = node.TimeoutTests,
                    PassRate = node.PassRate,
                    Health = node.Health,
                });
            }
            catch
            {
                // Skip builds that fail to parse
            }
        }

        var ordered = buildResults.OrderBy(b => b.Date).ToList();

        return new TrendReport
        {
            Builds = ordered,
            WeeklySummaries = GroupByWeek(ordered),
            MonthlySummaries = GroupByMonth(ordered),
            GoodThreshold = _config.GoodThreshold,
            WarningThreshold = _config.WarningThreshold,
        };
    }

    /// <summary>
    /// Generates per-UseCase trend data across builds.
    /// Shows how each test suite trends independently.
    /// </summary>
    public Dictionary<string, List<UseCaseTrendEntry>> AnalyzePerUseCaseTrends(
        string resultsRootPath, TrxResultsParser parser, int maxBuilds = 20)
    {
        var builds = parser.DiscoverBuilds(resultsRootPath);
        var useCaseTrends = new Dictionary<string, List<UseCaseTrendEntry>>();

        foreach (var (buildNumber, path, modified) in builds.Take(maxBuilds))
        {
            try
            {
                var node = parser.ParseBuildFolder(path);
                foreach (var uc in node.UseCases)
                {
                    if (!useCaseTrends.ContainsKey(uc.UseCaseName))
                        useCaseTrends[uc.UseCaseName] = new();

                    useCaseTrends[uc.UseCaseName].Add(new UseCaseTrendEntry
                    {
                        BuildNumber = buildNumber,
                        Date = modified,
                        Total = uc.Total,
                        Passed = uc.Passed,
                        Failed = uc.Failed,
                        PassRate = uc.PassRate,
                    });
                }
            }
            catch
            {
                // Skip builds that fail to parse
            }
        }

        foreach (var key in useCaseTrends.Keys.ToList())
            useCaseTrends[key] = useCaseTrends[key].OrderBy(e => e.Date).ToList();

        return useCaseTrends;
    }

    private List<PeriodSummary> GroupByWeek(List<BuildTrendEntry> builds)
    {
        return builds
            .GroupBy(b => $"{b.Date.Year}-W{System.Globalization.ISOWeek.GetWeekOfYear(b.Date):D2}")
            .Select(g => new PeriodSummary
            {
                Period = g.Key,
                BuildCount = g.Count(),
                TotalTests = g.Sum(b => b.TotalTests),
                TotalPassed = g.Sum(b => b.PassedTests),
                TotalFailed = g.Sum(b => b.FailedTests),
                AvgPassRate = g.Average(b => b.PassRate),
            })
            .OrderBy(s => s.Period)
            .ToList();
    }

    private List<PeriodSummary> GroupByMonth(List<BuildTrendEntry> builds)
    {
        return builds
            .GroupBy(b => b.Date.ToString("yyyy-MM"))
            .Select(g => new PeriodSummary
            {
                Period = g.Key,
                BuildCount = g.Count(),
                TotalTests = g.Sum(b => b.TotalTests),
                TotalPassed = g.Sum(b => b.PassedTests),
                TotalFailed = g.Sum(b => b.FailedTests),
                AvgPassRate = g.Average(b => b.PassRate),
            })
            .OrderBy(s => s.Period)
            .ToList();
    }
}

public record BuildTrendEntry
{
    public string BuildNumber { get; init; } = "";
    public DateTime Date { get; init; }
    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public int FailedTests { get; init; }
    public int TimeoutTests { get; init; }
    public double PassRate { get; init; }
    public HealthStatus Health { get; init; }
}

public record TrendReport
{
    public List<BuildTrendEntry> Builds { get; init; } = new();
    public List<PeriodSummary> WeeklySummaries { get; init; } = new();
    public List<PeriodSummary> MonthlySummaries { get; init; } = new();
    public double GoodThreshold { get; init; } = 95.0;
    public double WarningThreshold { get; init; } = 85.0;
}

public record PeriodSummary
{
    public string Period { get; init; } = "";
    public int BuildCount { get; init; }
    public int TotalTests { get; init; }
    public int TotalPassed { get; init; }
    public int TotalFailed { get; init; }
    public double AvgPassRate { get; init; }
}

public record UseCaseTrendEntry
{
    public string BuildNumber { get; init; } = "";
    public DateTime Date { get; init; }
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public double PassRate { get; init; }
}
