using System.Text.Json;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Execution;

/// <summary>Turns a completed impact-mapping run into a trigger file a WatchItem pipeline can execute (R3).</summary>
public interface IRunPlanWriter
{
    /// <summary>Writes the run manifest into <paramref name="watchFolder"/>, or null when nothing is runnable.</summary>
    Task<RunPlanResult?> WriteAsync(ImpactMappingResult result, string watchFolder, CancellationToken ct);
}

/// <summary>The manifest that was written and what it covers.</summary>
public sealed record RunPlanResult(
    string ManifestPath, string TestListPath, int AutomatedCount, int ManualCount, bool FilterInlined);

/// <summary>
/// Writes an impact selection as a layered-config-shaped JSON trigger file
/// (docs/impact/Regression-Selection-Algorithm.md §4.3). The controller already watches folders, fires
/// WatchItem events and resolves trigger parameters at <see cref="ParameterRank.TriggerFile"/>, so a
/// selection needs no second execution path — only a file in the right shape.
/// <para>
/// Written whole via <see cref="JsonSerializer"/> to a .tmp and renamed into place: never appended to
/// (appending CSV to JSON has taken this system down), and never observed half-written by the watcher.
/// </para>
/// </summary>
public sealed class RunPlanWriter : IRunPlanWriter
{
    private const string AutomatedStatus = "Automated";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ImpactMappingOptions.SelectionOptions _selection;
    private readonly IAppLogger _logger;

    /// <summary>Creates the writer from impact-mapping options.</summary>
    public RunPlanWriter(IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _selection = options.Value.Selection;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RunPlanResult?> WriteAsync(ImpactMappingResult result, string watchFolder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFolder);

        List<MappedTestCase> automated = result.MappedTestCases
            .Where(m => string.Equals(m.TestCase.AutomationStatus, AutomatedStatus, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(m.TestCase.AutomatedTestName))
            .ToList();
        List<MappedTestCase> manual = result.MappedTestCases.Except(automated).ToList();

        if (automated.Count == 0)
        {
            _logger.Warn("ImpactRunPlan",
                $"Run {result.RunId} selected {result.MappedTestCases.Count} test case(s) but none are automated — nothing to trigger.");
            return null;
        }

        Directory.CreateDirectory(watchFolder);

        List<string> testNames = automated
            .Select(m => m.TestCase.AutomatedTestName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string stem = $"impact-run-{result.RunId:D}";
        string testListPath = Path.Combine(watchFolder, $"{stem}.tests.txt");
        await File.WriteAllLinesAsync(testListPath, testNames, ct).ConfigureAwait(false);

        string filter = string.Join('|', testNames.Select(n => $"FullyQualifiedName={n}"));
        bool inlined = filter.Length <= _selection.MaxInlineFilterChars;

        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["_ImpactRunId"] = result.RunId.ToString("D"),
            ["_ImpactAreaId"] = result.Area.AreaId,
            ["_ImpactRiskTier"] = result.Area.RiskTier.ToString(),
            ["_ImpactTier"] = result.Tier.ToString(),
            ["_TestCaseIds"] = string.Join(',', automated.Select(m => m.TestCase.Item.Id)),
            ["_TestListFile"] = testListPath,
            ["_AutomatedCount"] = automated.Count.ToString(),
            ["_ManualCount"] = manual.Count.ToString(),
        };

        // Only inline the filter when a command line can actually carry it; the list file is always
        // written so a batch step has a path that works regardless of selection size.
        if (inlined)
        {
            global["_TestFilter"] = filter;
        }
        else
        {
            _logger.Warn("ImpactRunPlan",
                $"Run {result.RunId} filter is {filter.Length} chars (limit {_selection.MaxInlineFilterChars}); " +
                $"use [_TestListFile] instead of [_TestFilter].");
        }

        if (manual.Count > 0)
        {
            global["_ManualTestCaseIds"] = string.Join(',', manual.Select(m => m.TestCase.Item.Id));
        }

        var config = new PipelineParameterConfig { Version = 1, Global = global };
        string manifestPath = Path.Combine(watchFolder, $"{stem}.json");
        string tempPath = manifestPath + ".tmp";

        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(config, WriteOptions), ct).ConfigureAwait(false);
        File.Move(tempPath, manifestPath, overwrite: true);

        _logger.Info("ImpactRunPlan",
            $"Run {result.RunId}: wrote {automated.Count} automated ({manual.Count} manual) to {manifestPath}");

        return new RunPlanResult(manifestPath, testListPath, automated.Count, manual.Count, inlined);
    }
}
