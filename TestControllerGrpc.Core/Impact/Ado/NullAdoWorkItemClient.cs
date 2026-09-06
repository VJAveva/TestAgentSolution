using System.Runtime.CompilerServices;

namespace TestControllerGrpc.Core.Impact.Ado;

/// <summary>
/// No-op <see cref="IAdoWorkItemClient"/> used when Azure DevOps is not configured (no <c>Ado</c> section or
/// <c>Ado:Enabled=false</c>), so the <c>AddImpactMapping</c> graph resolves and the host starts under
/// <c>ValidateOnBuild</c> instead of failing because <c>AdoClient</c> is unregistered. The impact engine then
/// degrades to "no external work items" — mirroring the Null* provider defaults elsewhere in the engine.
/// </summary>
public sealed class NullAdoWorkItemClient : IAdoWorkItemClient
{
    public Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

    public Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(
        IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AdoWorkItemRef>>(Array.Empty<AdoWorkItemRef>());

    public Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TestCaseCandidate>>(Array.Empty<TestCaseCandidate>());

    public Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FeatureCandidate>>(Array.Empty<FeatureCandidate>());

    public Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>>());

    public async IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(
        string workItemType, DateTimeOffset since, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
