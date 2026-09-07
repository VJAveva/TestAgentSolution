using System.Text.Json;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Ado.Reporting;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Workstream A / P20. The cases that matter are the malformed ones: a cycle must terminate, a deep chain
/// must degrade to ORPHAN rather than throw, and neither may drop the work item.
/// </summary>
public class WorkItemHierarchyResolverTests
{
    private sealed class FakeQueries : IWorkItemQueries
    {
        private readonly Dictionary<int, (string Type, int? Parent)> _graph;

        public FakeQueries(Dictionary<int, (string Type, int? Parent)> graph) => _graph = graph;

        public List<int> RequestedBatchSizes { get; } = [];
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<AdoWorkItemDto>> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AdoWorkItemDto>> GetWithRelationsAsync(IReadOnlyList<int> ids, CancellationToken ct)
        {
            CallCount++;
            RequestedBatchSizes.Add(ids.Count);

            var result = new List<AdoWorkItemDto>();
            foreach (int id in ids)
            {
                if (!_graph.TryGetValue(id, out var node)) continue;

                var dto = new AdoWorkItemDto
                {
                    Id = id,
                    Fields = new Dictionary<string, JsonElement>
                    {
                        ["System.Title"] = JsonDocument.Parse($"\"WI {id}\"").RootElement,
                        ["System.WorkItemType"] = JsonDocument.Parse($"\"{node.Type}\"").RootElement,
                    },
                };

                if (node.Parent is { } p)
                {
                    dto.Relations.Add(new AdoWorkItemRelationDto
                    {
                        Rel = "System.LinkTypes.Hierarchy-Reverse",
                        Url = $"https://dev.azure.com/org/_apis/wit/workItems/{p}",
                    });
                }

                result.Add(dto);
            }

            return Task.FromResult<IReadOnlyList<AdoWorkItemDto>>(result);
        }
    }

    private static WorkItemHierarchyResolver Build(
        Dictionary<int, (string Type, int? Parent)> graph, out FakeQueries queries, int maxDepth = 6)
    {
        queries = new FakeQueries(graph);
        return new WorkItemHierarchyResolver(queries, new CodeChurnWorkItemPolicy { MaxHierarchyDepth = maxDepth });
    }

    [Fact]
    public async Task ResolveAsync_Should_FindFeature_When_NormalChain()
    {
        // Task 1 -> Story 2 -> Feature 3
        var resolver = Build(new()
        {
            [1] = ("Task", 2),
            [2] = ("User Story", 3),
            [3] = ("Feature", null),
        }, out _);

        WorkItemHierarchy result = await resolver.ResolveAsync([1], CancellationToken.None);

        Assert.Empty(result.OrphanIds);
        Assert.Equal([2, 3], result.AncestorsById[1].Select(a => a.Id));
        Assert.Equal("Feature", result.AncestorsById[1][^1].WorkItemType);
    }

    [Fact]
    public async Task ResolveAsync_Should_ReportOrphan_When_NoFeatureAncestor()
    {
        var resolver = Build(new()
        {
            [1] = ("Task", 2),
            [2] = ("User Story", null),
        }, out _);

        WorkItemHierarchy result = await resolver.ResolveAsync([1], CancellationToken.None);

        // Orphan, not an error and not a dropped row.
        Assert.Contains(1, result.OrphanIds);
        Assert.Single(result.AncestorsById[1]);
    }

    [Fact]
    public async Task ResolveAsync_Should_Terminate_When_GraphHasCycle()
    {
        // Malformed ADO links do happen; without a visited set this spins forever.
        var resolver = Build(new()
        {
            [1] = ("Task", 2),
            [2] = ("User Story", 1),
        }, out _);

        WorkItemHierarchy result = await resolver.ResolveAsync([1], CancellationToken.None);

        Assert.Contains(1, result.OrphanIds);
    }

    [Fact]
    public async Task ResolveAsync_Should_ReportOrphan_When_DepthExceeded()
    {
        // Chain of 5 with the Feature at the very top, but a depth budget of 2.
        var graph = new Dictionary<int, (string, int?)>
        {
            [1] = ("Task", 2),
            [2] = ("Task", 3),
            [3] = ("Task", 4),
            [4] = ("User Story", 5),
            [5] = ("Feature", null),
        };
        var resolver = Build(graph, out _, maxDepth: 2);

        WorkItemHierarchy result = await resolver.ResolveAsync([1], CancellationToken.None);

        Assert.Contains(1, result.OrphanIds);
    }

    [Fact]
    public async Task ResolveAsync_Should_WalkSharedParentOnce_When_ManyChildren()
    {
        // 40 stories under one Feature: the Feature must be fetched once, not 40 times.
        var graph = new Dictionary<int, (string, int?)> { [1000] = ("Feature", null) };
        var children = new List<int>();
        for (int i = 1; i <= 40; i++)
        {
            graph[i] = ("User Story", 1000);
            children.Add(i);
        }

        var resolver = Build(graph, out FakeQueries queries);

        WorkItemHierarchy result = await resolver.ResolveAsync(children, CancellationToken.None);

        Assert.Empty(result.OrphanIds);
        // One call for the 40 children, one for the shared parent.
        Assert.Equal(2, queries.CallCount);
        Assert.Equal([40, 1], queries.RequestedBatchSizes);
    }

    [Fact]
    public async Task ResolveAsync_Should_RequestEachIdOnce_When_DuplicatesSupplied()
    {
        // Chunking at ADO's 200-id limit belongs to WorkItemQueries; the resolver's contract is one
        // deduplicated request per level. WorkItemQueriesTests covers the 200/201 boundary.
        var graph = new Dictionary<int, (string, int?)>
        {
            [1] = ("Feature", null),
            [2] = ("Feature", null),
            [3] = ("Feature", null),
        };
        var resolver = Build(graph, out FakeQueries queries);

        await resolver.ResolveAsync([1, 1, 2, 2, 3], CancellationToken.None);

        Assert.Equal(1, queries.CallCount);
        Assert.Equal([3], queries.RequestedBatchSizes);
    }

    [Fact]
    public async Task ResolveAsync_Should_ReturnEmpty_When_NoIds()
    {
        var resolver = Build([], out FakeQueries queries);

        WorkItemHierarchy result = await resolver.ResolveAsync([], CancellationToken.None);

        Assert.Empty(result.AncestorsById);
        Assert.Equal(0, queries.CallCount);
    }
}
