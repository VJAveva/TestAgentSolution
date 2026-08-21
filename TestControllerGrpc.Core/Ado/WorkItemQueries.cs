using TestControllerGrpc.Ado.Dto;

namespace TestControllerGrpc.Ado;

public interface IWorkItemQueries
{
    /// <summary>Fetches work items by id, chunking every 200 ids per ADO's batch limit.</summary>
    Task<IReadOnlyList<AdoWorkItemDto>> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct);
}

public sealed class WorkItemQueries : IWorkItemQueries
{
    private const int MaxBatchSize = 200;
    private static readonly string[] Fields = ["System.Title", "System.WorkItemType", "System.State", "System.CreatedDate"];

    private readonly AdoClient _client;

    public WorkItemQueries(AdoClient client) => _client = client;

    public async Task<IReadOnlyList<AdoWorkItemDto>> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var results = new List<AdoWorkItemDto>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(MaxBatchSize))
        {
            var path = _client.OrgApiPath("wit/workitemsbatch?api-version=7.1");
            var body = new { ids = chunk, fields = Fields };
            var response = await _client.PostAsync<AdoListResponse<AdoWorkItemDto>>(path, body, ct);
            results.AddRange(response.Value);
        }
        return results;
    }
}
