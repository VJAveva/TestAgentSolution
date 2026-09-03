using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
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
    private readonly string _organization;
    private readonly IAppLogger _logger;

    /// <summary>Creates the matcher over the impact engine and the ADO organization (for Test Case deep links).</summary>
    public RegressionImpactMatcher(IImpactTestMappingService engine, IOptions<AdoOptions> adoOptions, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(adoOptions);
        _engine = engine;
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
            return result.MappedTestCases
                .Select(m => Project(area.DisplayName, m))
                .OrderByDescending(m => m.ConfidencePercent)
                .ToList();
        }
        catch (OperationCanceledException)
        {
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

    private static ImpactedArea ToImpactedArea(SubsystemRow row)
    {
        DateTimeOffset last = row.Changes.Count > 0 ? row.Changes.Max(c => c.ObservedUtc) : DateTimeOffset.UnixEpoch;
        var churn = new ChurnMetrics(
            LinesAdded: 0, LinesDeleted: 0, FilesTouched: row.TotalFilesModified,
            CommitCount: row.Changes.Count, DistinctAuthorCount: 0, LastChangedUtc: last);

        return new ImpactedArea(
            AreaId: row.Component,
            DisplayName: row.Component,
            Subsystem: string.IsNullOrWhiteSpace(row.Subsystem) ? null : row.Subsystem,
            Vob: null,
            ChangedPaths: row.FilesModified,
            DeclaredRegressionAreas: row.RegressionAreas ?? [],
            RiskTier: RiskTier.Medium,
            Churn: churn);
    }

    private static ChangePayload ToChangePayload(SubsystemRow row)
    {
        List<string> commitMessages = row.Changes
            .Select(c => c.Summary)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        List<int> workItemIds = row.Changes
            .SelectMany(c => c.WorkItems)
            .Select(w => w.Id)
            .Distinct()
            .ToList();

        return new ChangePayload(
            PullRequestId: null,
            PrTitle: null,
            PrDescription: null,
            CommitMessages: commitMessages,
            Diffs: [],
            LinkedWorkItemIds: workItemIds);
    }

    private ImpactedTestCaseMatch Project(string area, MappedTestCase m)
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
            MatchReason: m.Judgement?.Reason ?? "");
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
