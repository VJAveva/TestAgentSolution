using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact.Risk;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact;

/// <summary>
/// Default <see cref="IRegressionImpactMatcher"/>: adapts a <see cref="SubsystemRow"/> into the engine's
/// <see cref="ImpactedArea"/> / <see cref="ChangePayload"/> inputs, runs <see cref="IImpactTestMappingService"/>
/// at the Targeted tier, and projects the selected <see cref="MappedTestCase"/>s to <see cref="ImpactedTestCaseMatch"/>.
/// Linked work items are forwarded as Tier-0 anchors so a component still yields matches when the retrieval
/// index is thin. Every engine call is guarded — a failure degrades to an empty result plus a logged warning.
/// </summary>
public sealed class RegressionImpactMatcher : IRegressionImpactMatcher
{
    private const int ComponentConcurrency = 4;
    private const int MaxStepsChars = 400;
    private const SelectionTier Tier = SelectionTier.Targeted;

    private readonly IImpactTestMappingService _engine;
    private readonly IRegressionRiskScorer _riskScorer;
    private readonly string _organization;
    private readonly IAppLogger _logger;

    /// <summary>Creates the matcher over the impact engine and the ADO organization (for Test Case deep links).</summary>
    public RegressionImpactMatcher(
        IImpactTestMappingService engine,
        IRegressionRiskScorer riskScorer,
        IOptions<AdoOptions> adoOptions,
        IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(adoOptions);
        _engine = engine;
        _riskScorer = riskScorer;
        _organization = adoOptions.Value.Organization ?? "";
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImpactedTestCaseMatch>> MatchAsync(SubsystemRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        ImpactedArea area = ToImpactedArea(row);
        ChangePayload payload = ToChangePayload(row);

        // No anchors and nothing to retrieve on — skip the engine entirely rather than force a throw.
        if (payload.LinkedWorkItemIds.Count == 0 && payload.CommitMessages.Count == 0 && area.ChangedPaths.Count == 0)
            return [];

        try
        {
            ImpactMappingResult result = await _engine.MapAsync(area, payload, Tier, ct).ConfigureAwait(false);
            IReadOnlyDictionary<int, IReadOnlyList<RegressionWorkItemRef>> linkedByTestCase =
                MapAnchorsToWorkItems(result, row);
            return result.MappedTestCases
                .Select(m => Project(area.DisplayName, m, linkedByTestCase))
                .OrderByDescending(m => m.ConfidencePercent)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImpactIndexUnavailableException)
        {
            // Must escape the blanket catch below. An unusable index would otherwise be reported as
            // "no impacted test cases" — indistinguishable from a genuine empty result, which is the
            // worst outcome for recall-biased test selection.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("ImpactMatch", $"Test-case matching failed for '{row.Component}': {ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImpactedTestCaseMatch>> MatchManyAsync(IReadOnlyList<SubsystemRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return [];

        using var gate = new SemaphoreSlim(ComponentConcurrency);
        IEnumerable<Task<IReadOnlyList<ImpactedTestCaseMatch>>> tasks = rows.Select(async row =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await MatchAsync(row, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        // Task.WhenAll preserves the input order, so rows stay grouped by component in the output.
        IReadOnlyList<ImpactedTestCaseMatch>[] perRow = await Task.WhenAll(tasks).ConfigureAwait(false);
        return perRow.SelectMany(x => x).ToList();
    }

    private ImpactedArea ToImpactedArea(SubsystemRow row)
    {
        RegressionRiskAssessment risk = _riskScorer.Score(row, DateTimeOffset.UtcNow);

        _logger.Info(
            "ImpactRisk",
            $"'{row.Component}' risk {risk.Score:0.###} => {risk.Tier} " +
            $"({string.Join(", ", risk.Components.Select(c => $"{c.Name}={c.Raw:0.##}"))})");

        return new ImpactedArea(
            AreaId: row.Component,
            DisplayName: row.Component,
            Subsystem: string.IsNullOrWhiteSpace(row.Subsystem) ? null : row.Subsystem,
            Vob: null,
            ChangedPaths: row.FilesModified,
            DeclaredRegressionAreas: row.RegressionAreas ?? [],
            RiskTier: risk.Tier,
            Churn: risk.Churn);
    }

    private static ChangePayload ToChangePayload(SubsystemRow row)
    {
        List<string> commitMessages = row.Changes
            .Select(c => c.Summary)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        List<RegressionWorkItemRef> workItems = row.Changes
            .SelectMany(c => c.WorkItems)
            .GroupBy(w => w.Id)
            .Select(g => g.First())
            .ToList();

        // Feed every change subject + work-item title (functional English — the vocabulary test cases use) as the
        // PR narrative so it drives retrieval and grounds HyDE/rerank, not just file paths and the first subject.
        List<string> narrative = commitMessages
            .Concat(workItems.Select(w => w.Title))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return new ChangePayload(
            PullRequestId: null,
            PrTitle: narrative.FirstOrDefault(),
            PrDescription: narrative.Count > 1 ? string.Join(". ", narrative.Skip(1)) : null,
            CommitMessages: commitMessages,
            Diffs: [],
            LinkedWorkItemIds: workItems.Select(w => w.Id).ToList());
    }

    private ImpactedTestCaseMatch Project(
        string area, MappedTestCase m, IReadOnlyDictionary<int, IReadOnlyList<RegressionWorkItemRef>> linkedByTestCase)
    {
        int grade = m.Judgement?.Grade ?? 0;
        string matchType = grade >= 3 ? "full" : "partial";
        int pct = Math.Clamp((int)Math.Round(ConfidenceOf(m) * 100.0), 0, 100);
        string? url = m.TestCase.Item.Id > 0 && _organization.Length > 0
            ? $"https://dev.azure.com/{_organization}/_workitems/edit/{m.TestCase.Item.Id}"
            : null;

        return new ImpactedTestCaseMatch(
            ImpactedArea: area,
            TestCaseId: m.TestCase.Item.Id,
            TestCaseTitle: m.TestCase.Item.Title,
            Description: TrimSteps(m.TestCase.StepsText),
            TestCaseUrl: url,
            ParentFeatureId: m.FeatureId,
            MatchType: matchType,
            ConfidencePercent: pct,
            MatchReason: m.Judgement?.Reason ?? "",
            LinkedWorkItems: linkedByTestCase.GetValueOrDefault(m.TestCase.Item.Id));
    }

    // AnchorEdge carries the linked work-item id in FeatureId; the row already carries that work item's type,
    // title and URL, so the join gives each test case the Bug/IMS/Story that put it in scope.
    private static IReadOnlyDictionary<int, IReadOnlyList<RegressionWorkItemRef>> MapAnchorsToWorkItems(
        ImpactMappingResult result, SubsystemRow row)
    {
        Dictionary<int, RegressionWorkItemRef> byId = row.Changes
            .SelectMany(c => c.WorkItems)
            .GroupBy(w => w.Id)
            .ToDictionary(g => g.Key, g => g.First());

        return result.Anchors.Edges
            .Where(e => e.Source == AnchorSource.LinkedWorkItem && e.FeatureId is { } id && byId.ContainsKey(id))
            .GroupBy(e => e.TestCaseId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RegressionWorkItemRef>)g
                    .Select(e => byId[e.FeatureId!.Value])
                    .DistinctBy(w => w.Id)
                    .OrderBy(w => w.Id)
                    .ToList());
    }

    // The calibrated ranking score drives the confidence % (it is what orders the list); the rerank
    // judgement's own confidence is the fallback when the score is not a usable [0,1] magnitude.
    private static double ConfidenceOf(MappedTestCase m)
    {
        double score = m.FinalScore;
        if (double.IsFinite(score) && score > 0.0)
            return Math.Min(score, 1.0);
        return m.Judgement?.Confidence ?? 0.0;
    }

    private static string? TrimSteps(string? steps)
    {
        if (string.IsNullOrWhiteSpace(steps))
            return null;
        string collapsed = steps.Trim();
        return collapsed.Length <= MaxStepsChars ? collapsed : collapsed[..MaxStepsChars] + "\u2026";
    }
}
