using TestControllerGrpc.Models;
namespace TestControllerGrpc.Services;

public class FlakyTestDetector
{
    /// <summary>
    /// Identifies tests that have been failing frequently over recent builds.
    /// A "flaky" test is one that fails in 2+ of the last N builds but not ALL of them
    /// (consistent failures are bugs, intermittent failures are flaky).
    /// </summary>
    public List<FlakyTestAlert> DetectFlakyTests(
        string resultsRootPath, TrxResultsParser parser,
        int recentBuilds = 5, int minFailures = 2)
    {
        var builds = parser.DiscoverBuilds(resultsRootPath)
            .OrderByDescending(b => b.Modified)
            .Take(recentBuilds)
            .ToList();

        var history = new Dictionary<string, List<(string Build, string Outcome, DateTime Date)>>();
        var testUseCaseMap = new Dictionary<string, string>();
        var testLastError = new Dictionary<string, string>();

        foreach (var (buildNumber, path, modified) in builds)
        {
            try
            {
                var node = parser.ParseBuildFolder(path);
                foreach (var uc in node.UseCases)
                {
                    foreach (var test in uc.TestResults)
                    {
                        if (!history.ContainsKey(test.TestName))
                            history[test.TestName] = new();

                        history[test.TestName].Add((buildNumber, test.Outcome, modified));
                        testUseCaseMap[test.TestName] = uc.UseCaseName;

                        if (test.Outcome == "Failed" && !string.IsNullOrEmpty(test.ErrorMessage))
                            testLastError[test.TestName] = test.ErrorMessage;
                    }
                }
            }
            catch
            {
                // Skip builds that fail to parse
            }
        }

        var alerts = new List<FlakyTestAlert>();

        foreach (var (testName, entries) in history)
        {
            var failCount = entries.Count(e => e.Outcome == "Failed");
            var passCount = entries.Count(e => e.Outcome == "Passed");
            var totalRuns = entries.Count;

            if (failCount < minFailures) continue;

            // Flaky = fails sometimes but not always
            // Consistent = fails in ALL runs
            var classification = failCount == totalRuns ? "Consistent"
                : failCount >= totalRuns * 0.5 ? "Frequent"
                : "Intermittent";

            alerts.Add(new FlakyTestAlert
            {
                TestName = testName,
                UseCaseName = testUseCaseMap.GetValueOrDefault(testName, ""),
                FailureCount = failCount,
                TotalRuns = totalRuns,
                FlakyRate = (double)failCount / totalRuns * 100,
                FailedInBuilds = entries.Where(e => e.Outcome == "Failed")
                    .Select(e => e.Build).ToList(),
                LastError = testLastError.GetValueOrDefault(testName, ""),
                Classification = classification,
            });
        }

        return alerts.OrderByDescending(a => a.FlakyRate)
            .ThenByDescending(a => a.FailureCount)
            .ToList();
    }
}

public record FlakyTestAlert
{
    public string TestName { get; init; } = "";
    public string UseCaseName { get; init; } = "";
    public int FailureCount { get; init; }
    public int TotalRuns { get; init; }
    public double FlakyRate { get; init; }
    public List<string> FailedInBuilds { get; init; } = new();
    public string LastError { get; init; } = "";
    /// <summary>Consistent, Frequent, or Intermittent.</summary>
    public string Classification { get; init; } = "";
}
