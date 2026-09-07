using System.Collections.Concurrent;
using TestControllerGrpc.Ado.Dto;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>Where a work item sits relative to its nearest Feature ancestor.</summary>
/// <param name="AncestorsById">
/// Nearest non-excluded ancestor chain per requested id, nearest first. Empty when the item is an orphan.
/// </param>
/// <param name="OrphanIds">Ids with no Feature ancestor within the configured depth. Never an error, never dropped.</param>
public sealed record WorkItemHierarchy(
    IReadOnlyDictionary<int, IReadOnlyList<WorkItemNode>> AncestorsById,
    IReadOnlySet<int> OrphanIds);

/// <summary>One work item in an ancestry chain.</summary>
public sealed record WorkItemNode(int Id, string Title, string? WorkItemType);

public interface IWorkItemHierarchyResolver
{
    Task<WorkItemHierarchy> ResolveAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
}

/// <summary>
/// Used when ADO ingest is off (mock provider): there is no hierarchy to read, so every work item reports as
/// an orphan rather than the report failing. Exclusion accounting still works.
/// </summary>
public sealed class NullWorkItemHierarchyResolver : IWorkItemHierarchyResolver
{
    public Task<WorkItemHierarchy> ResolveAsync(IReadOnlyCollection<int> ids, CancellationToken ct) =>
        Task.FromResult(new WorkItemHierarchy(
            new Dictionary<int, IReadOnlyList<WorkItemNode>>(),
            ids.ToHashSet()));
}

/// <summary>
/// Walks <c>System.LinkTypes.Hierarchy-Reverse</c> upward from each work item until it reaches a Feature,
/// exceeds the configured depth, or hits a root.
/// </summary>
/// <remarks>
/// Written for the fan-out this report actually produces: a Feature with forty stories must be walked once,
/// not forty times. Ids are deduplicated before every request, each level is fetched as a single batched call,
/// and resolved ancestry is memoised for the lifetime of the instance.
///
/// A malformed ADO link graph can contain cycles, so the walk carries a visited set. Hitting the depth limit
/// yields an ORPHAN, not an exception and not a silent drop.
/// </remarks>
public sealed class WorkItemHierarchyResolver : IWorkItemHierarchyResolver
{
    private const string FeatureType = "Feature";

    private readonly IWorkItemQueries _queries;
    private readonly CodeChurnWorkItemPolicy _policy;
    private readonly ConcurrentDictionary<int, AdoWorkItemDto> _cache = new();

    public WorkItemHierarchyResolver(IWorkItemQueries queries, CodeChurnWorkItemPolicy policy)
    {
        _queries = queries;
        _policy = policy;
    }

    public async Task<WorkItemHierarchy> ResolveAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var roots = ids.Distinct().ToArray();
        if (roots.Length == 0)
            return new WorkItemHierarchy(new Dictionary<int, IReadOnlyList<WorkItemNode>>(), new HashSet<int>());

        await HydrateAsync(roots, ct).ConfigureAwait(false);

        // Level-order so each generation of parents is one batched request, however wide the fan-in.
        var frontier = roots.ToHashSet();
        for (int depth = 0; depth < _policy.MaxHierarchyDepth && frontier.Count > 0; depth++)
        {
            var parents = new HashSet<int>();
            foreach (int id in frontier)
            {
                if (_cache.TryGetValue(id, out AdoWorkItemDto? dto)
                    && dto.ParentId is { } parentId
                    && !_cache.ContainsKey(parentId))
                {
                    parents.Add(parentId);
                }
            }

            if (parents.Count == 0) break;
            await HydrateAsync(parents, ct).ConfigureAwait(false);
            frontier = parents;
        }

        var ancestors = new Dictionary<int, IReadOnlyList<WorkItemNode>>(roots.Length);
        var orphans = new HashSet<int>();

        foreach (int id in roots)
        {
            List<WorkItemNode> chain = WalkToFeature(id, out bool foundFeature);
            ancestors[id] = chain;
            if (!foundFeature) orphans.Add(id);
        }

        return new WorkItemHierarchy(ancestors, orphans);
    }

    private List<WorkItemNode> WalkToFeature(int startId, out bool foundFeature)
    {
        var chain = new List<WorkItemNode>();
        var visited = new HashSet<int> { startId };
        foundFeature = false;

        int? current = _cache.TryGetValue(startId, out AdoWorkItemDto? start) ? start.ParentId : null;

        for (int depth = 0; current is { } id && depth < _policy.MaxHierarchyDepth; depth++)
        {
            // A malformed link graph can loop; stop rather than spin.
            if (!visited.Add(id)) break;
            if (!_cache.TryGetValue(id, out AdoWorkItemDto? dto)) break;

            chain.Add(new WorkItemNode(id, dto.Title ?? $"Work item {id}", dto.WorkItemType));

            if (string.Equals(dto.WorkItemType, FeatureType, StringComparison.OrdinalIgnoreCase))
            {
                foundFeature = true;
                break;
            }

            current = dto.ParentId;
        }

        return chain;
    }

    private async Task HydrateAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var missing = ids.Where(id => !_cache.ContainsKey(id)).Distinct().ToArray();
        if (missing.Length == 0) return;

        IReadOnlyList<AdoWorkItemDto> fetched = await _queries.GetWithRelationsAsync(missing, ct).ConfigureAwait(false);
        foreach (AdoWorkItemDto dto in fetched)
            _cache[dto.Id] = dto;
    }
}
