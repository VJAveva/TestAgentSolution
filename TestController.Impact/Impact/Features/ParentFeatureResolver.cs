using TestControllerGrpc.Core.Impact.Ado;
using TestControllerGrpc.Core.Impact.Ranking;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Features;

/// <summary>Features discovered by walking from matched test cases to their parents, plus the evidence and orphans.</summary>
public sealed record ParentResolution(
    IReadOnlyList<FeatureCandidate> Features,
    IReadOnlyList<TestCaseEvidence> Evidence,
    IReadOnlyList<TestCaseCandidate> Orphans);

/// <summary>Resolves the parent features of ranked test cases (P15).</summary>
public interface IParentFeatureResolver
{
    /// <summary>Walks each ranked test case to its parent feature, emitting back-reference evidence.</summary>
    Task<ParentResolution> ResolveAsync(
        KeywordGroup group, IReadOnlyList<Scored<TestCaseCandidate>> rankedTestCases, CancellationToken ct);
}

/// <summary>
/// The test-case back-reference branch (P15): it finds features whose titles never match the change but whose
/// test cases do. Parent ids already carried by a test case are used directly; the rest are resolved in a
/// single batched ADO call before giving up. Resolved features are hydrated once, deduplicated, and stamped
/// with <see cref="FeatureDiscoveryPath.TestCaseBackReference"/>. Each resolved test case yields one
/// <see cref="TestCaseEvidence"/> (carrying its retrieval score) that feeds the P14 tcEvidence signal.
/// Test cases with no resolvable parent are returned as orphans (selectable later under feature id -1), never
/// discarded — recall bias.
/// </summary>
public sealed class ParentFeatureResolver : IParentFeatureResolver
{
    private readonly IAdoWorkItemClient _ado;
    private readonly IAppLogger _logger;

    /// <summary>Creates the resolver over the ADO work-item client.</summary>
    public ParentFeatureResolver(IAdoWorkItemClient ado, IAppLogger logger)
    {
        _ado = ado ?? throw new ArgumentNullException(nameof(ado));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ParentResolution> ResolveAsync(
        KeywordGroup group, IReadOnlyList<Scored<TestCaseCandidate>> rankedTestCases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(rankedTestCases);

        var parentByTestCase = new Dictionary<int, int>();
        var unresolved = new List<int>();
        foreach (Scored<TestCaseCandidate> scored in rankedTestCases)
        {
            TestCaseCandidate testCase = scored.Value;
            if (testCase.ParentFeatureId is int parentId)
            {
                parentByTestCase[testCase.Item.Id] = parentId;
            }
            else
            {
                unresolved.Add(testCase.Item.Id);
            }
        }

        if (unresolved.Count > 0)
        {
            IReadOnlyDictionary<int, int> resolved = await _ado.ResolveParentFeaturesAsync(unresolved, ct).ConfigureAwait(false);
            foreach ((int testCaseId, int featureId) in resolved)
            {
                parentByTestCase[testCaseId] = featureId;
            }
        }

        int[] featureIds = parentByTestCase.Values.Distinct().ToArray();
        IReadOnlyList<FeatureCandidate> hydrated = featureIds.Length > 0
            ? await _ado.GetFeaturesAsync(featureIds, ct).ConfigureAwait(false)
            : [];

        List<FeatureCandidate> features = hydrated
            .GroupBy(f => f.Item.Id)
            .Select(g => g.First() with
            {
                DiscoveryPath = FeatureDiscoveryPath.TestCaseBackReference,
                MatchedGroupIds = [group.GroupId],
            })
            .OrderBy(f => f.Item.Id)
            .ToList();

        var evidence = new List<TestCaseEvidence>();
        var orphans = new List<TestCaseCandidate>();
        foreach (Scored<TestCaseCandidate> scored in rankedTestCases)
        {
            TestCaseCandidate testCase = scored.Value;
            if (parentByTestCase.TryGetValue(testCase.Item.Id, out int featureId))
            {
                evidence.Add(new TestCaseEvidence(featureId, testCase.Item.Id, testCase.Item.Title, scored.Score));
            }
            else
            {
                orphans.Add(testCase);
            }
        }

        if (orphans.Count > 0)
        {
            _logger.Warn("ImpactParent",
                $"{orphans.Count} test case(s) resolved to no parent feature; retained as orphans (feature -1).");
        }

        return new ParentResolution(features, evidence, orphans);
    }
}
