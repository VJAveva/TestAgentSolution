using System.Runtime.CompilerServices;
using TestControllerGrpc.Core.Impact.Ado;

namespace TestControllerGrpc.Core.Impact.Testing;

/// <summary>
/// In-memory <see cref="IAdoWorkItemClient"/> for tests and fixtures (P26). Backed by simple dictionaries so a
/// whole feature/test-case link graph can be assembled deterministically without touching Azure DevOps.
/// </summary>
public sealed class FakeAdoWorkItemClient : IAdoWorkItemClient
{
    /// <summary>Features by work item id.</summary>
    public Dictionary<int, FeatureCandidate> Features { get; } = [];

    /// <summary>Test cases by work item id.</summary>
    public Dictionary<int, TestCaseCandidate> TestCases { get; } = [];

    /// <summary>Child test-case ids per feature id.</summary>
    public Dictionary<int, IReadOnlyList<int>> ChildrenByFeature { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FeatureCandidate>>(ids.Where(Features.ContainsKey).Select(id => Features[id]).ToList());

    /// <inheritdoc />
    public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestCaseCandidate>>(ids.Where(TestCases.ContainsKey).Select(id => TestCases[id]).ToList());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(
            ChildrenByFeature.Where(kv => featureIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, int>>(testCaseIds
            .Where(id => TestCases.TryGetValue(id, out TestCaseCandidate? tc) && tc.ParentFeatureId is not null)
            .ToDictionary(id => id, id => TestCases[id].ParentFeatureId!.Value));

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<int>>([.. TestCases.Keys.Concat(Features.Keys).Take(maxResults)]);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        var refs = ids
            .Select(id => Features.TryGetValue(id, out FeatureCandidate? f) ? f.Item
                : TestCases.TryGetValue(id, out TestCaseCandidate? t) ? t.Item : null)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        return Task.FromResult<IReadOnlyList<AdoWorkItemRef>>(refs);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(
        string workItemType, DateTimeOffset since, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        IEnumerable<AdoWorkItemRef> source = workItemType == "Test Case"
            ? TestCases.Values.Select(t => t.Item)
            : Features.Values.Select(f => f.Item);

        foreach (AdoWorkItemRef item in source.OrderBy(r => r.Id))
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask;
    }
}
