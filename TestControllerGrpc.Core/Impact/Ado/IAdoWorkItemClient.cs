namespace TestControllerGrpc.Core.Impact.Ado;

/// <summary>
/// Impact-specific Azure DevOps work-item queries (P05). Built on the existing <c>AdoClient</c> /
/// <c>IAdoTokenProvider</c> (credentials, retry and token redaction are handled there — P04), adding only
/// the Feature/Test-Case retrieval, steps flattening, link walking and incremental enumeration the engine needs.
/// </summary>
public interface IAdoWorkItemClient
{
    /// <summary>Runs a flat WIQL query and returns matching work item ids (capped at <paramref name="maxResults"/>).</summary>
    Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct);

    /// <summary>Batch-hydrates work items with the requested fields (always includes System.Rev for caching).</summary>
    Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(IReadOnlyCollection<int> ids,
        IReadOnlyCollection<string> fields, CancellationToken ct);

    /// <summary>Hydrates Test Cases with flattened steps, automation status, tags and parent feature.</summary>
    Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>Hydrates Features with description.</summary>
    Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>Resolves each test case's parent Feature via Hierarchy / TestedBy links (batched).</summary>
    Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(IReadOnlyCollection<int> testCaseIds, CancellationToken ct);

    /// <summary>Resolves each feature's child Test Cases via Hierarchy / TestedBy links (batched).</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(IReadOnlyCollection<int> featureIds, CancellationToken ct);

    /// <summary>Streams work items of a type changed on/after <paramref name="since"/>, oldest first (drives incremental indexing).</summary>
    IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(string workItemType, DateTimeOffset since,
        int pageSize, CancellationToken ct);

    /// <summary>
    /// Streams ids of work items now in one of <paramref name="states"/> and changed on/after
    /// <paramref name="since"/>. Drives the purge of documents whose work item was Removed or Closed after
    /// it was indexed. Defaults to empty so offline and fake clients need no change.
    /// </summary>
    IAsyncEnumerable<int> EnumerateIdsInStatesAsync(string workItemType, IReadOnlyCollection<string> states,
        DateTimeOffset since, CancellationToken ct) => AsyncEnumerable.Empty<int>();
}
