using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

public class ConsecutiveFailureDetector
{
    /// <summary>
    /// Scans builds in chronological order and identifies test cases
    /// that failed in N or more consecutive builds (counting from the most recent build).
    /// </summary>
    public List<ConsecutiveFailureAlert> Detect(
        string resultsRootPath, TrxResultsParser parser, int consecutiveThreshold = 2)
    {
        var builds = parser.DiscoverBuilds(resultsRootPath)
            .OrderBy(b => b.Modified).ToList();

        // Track: testName ? list of (buildNumber, failed?)
        var testHistory = new Dictionary<string, List<(string Build, bool Failed)>>();
        // Track: testName ? useCaseName (from last build)
        var testUseCaseMap = new Dictionary<string, string>();
        // Track: testName ? last error message
        var testLastError = new Dictionary<string, string>();

        foreach (var (buildNumber, path, _) in builds.TakeLast(10))
        {
            BuildNode node;
            try
            {
                node = parser.ParseBuildFolder(path);
            }
            catch
            {
                continue;
            }

            var allTests = node.UseCases.SelectMany(u =>
                u.TestResults.Select(t => (UseCase: u.UseCaseName, Test: t)));

            foreach (var (useCaseName, test) in allTests)
            {
                if (!testHistory.ContainsKey(test.TestName))
                    testHistory[test.TestName] = new();

                testHistory[test.TestName].Add(
                    (buildNumber, test.Outcome == "Failed"));

                testUseCaseMap[test.TestName] = useCaseName;

                if (test.Outcome == "Failed" && !string.IsNullOrEmpty(test.ErrorMessage))
                    testLastError[test.TestName] = test.ErrorMessage;
            }
        }

        // Find tests with N+ consecutive failures at the END of history
        var alerts = new List<ConsecutiveFailureAlert>();
        foreach (var (testName, history) in testHistory)
        {
            int streak = 0;
            var failedBuilds = new List<string>();
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Failed)
                {
                    streak++;
                    failedBuilds.Add(history[i].Build);
                }
                else break;
            }

            if (streak >= consecutiveThreshold)
            {
                alerts.Add(new ConsecutiveFailureAlert
                {
                    TestName = testName,
                    UseCaseName = testUseCaseMap.GetValueOrDefault(testName, "Unknown"),
                    ConsecutiveFailCount = streak,
                    FailedInBuilds = failedBuilds,
                    LastError = testLastError.GetValueOrDefault(testName, ""),
                    Priority = streak >= 5 ? "CRITICAL"
                             : streak >= 3 ? "HIGH" : "MEDIUM",
                });
            }
        }

        return alerts.OrderByDescending(a => a.ConsecutiveFailCount).ToList();
    }
}

public record ConsecutiveFailureAlert
{
    public string TestName { get; init; } = "";
    public string UseCaseName { get; init; } = "";
    public string Priority { get; init; } = "";
    public string LastError { get; init; } = "";
    public int ConsecutiveFailCount { get; init; }
    public List<string> FailedInBuilds { get; init; } = new();
}
