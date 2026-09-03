using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Ado;

/// <summary>
/// <see cref="IAdoWorkItemClient"/> over the existing <see cref="AdoClient"/> (auth/retry/redaction reused
/// from the CIRP ADO layer). Maps raw work-item fields into the impact contracts.
/// </summary>
public sealed class AdoImpactWorkItemClient : IAdoWorkItemClient
{
    private static readonly string[] RefFields =
        ["System.Title", "System.WorkItemType", "System.State", "System.AreaPath", "System.Rev"];
    private static readonly string[] TestCaseFields =
        ["System.Title", "System.WorkItemType", "System.State", "System.AreaPath", "System.Rev",
         "Microsoft.VSTS.TCM.Steps", "Microsoft.VSTS.TCM.AutomationStatus", "System.Tags", "System.Parent"];
    private static readonly string[] FeatureFields =
        ["System.Title", "System.WorkItemType", "System.State", "System.AreaPath", "System.Rev", "System.Description"];
    private static readonly string[] ChangeFields =
        ["System.Title", "System.WorkItemType", "System.State", "System.AreaPath", "System.Rev", "System.ChangedDate"];

    private readonly AdoClient _client;
    private readonly ImpactMappingOptions _options;
    private readonly IAppLogger _logger;

    public AdoImpactWorkItemClient(AdoClient client, IOptions<ImpactMappingOptions> options, IAppLogger logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<int>> QueryIdsAsync(string wiql, int maxResults, CancellationToken ct)
    {
        var top = Math.Max(1, maxResults);
        var path = _client.ProjectApiPath($"wit/wiql?api-version=7.1&$top={top}");
        var resp = await _client.PostAsync<WiqlResponse>(path, new { query = wiql }, ct);
        _logger.Info("Impact.Ado", $"WIQL returned {resp.WorkItems.Count} id(s).");
        return resp.WorkItems.Select(w => w.Id).Take(maxResults).ToList();
    }

    public async Task<IReadOnlyList<AdoWorkItemRef>> HydrateAsync(
        IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct)
        => (await HydrateDtosAsync(ids, fields, ct)).Select(ToRef).ToList();

    public async Task<IReadOnlyList<TestCaseCandidate>> GetTestCasesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var dtos = await HydrateDtosAsync(ids, TestCaseFields, ct);
        return dtos.Select(d => new TestCaseCandidate(
            ToRef(d),
            StepsFlattener.Flatten(Str(d, "Microsoft.VSTS.TCM.Steps")),
            Str(d, "Microsoft.VSTS.TCM.AutomationStatus"),
            IntOrNull(d, "System.Parent"),
            SplitTags(Str(d, "System.Tags")))).ToList();
    }

    public async Task<IReadOnlyList<FeatureCandidate>> GetFeaturesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var dtos = await HydrateDtosAsync(ids, FeatureFields, ct);
        return dtos.Select(d => new FeatureCandidate(
            ToRef(d),
            StripHtml(Str(d, "System.Description")),
            FeatureDiscoveryPath.None,
            [],
            0)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> ResolveParentFeaturesAsync(
        IReadOnlyCollection<int> testCaseIds, CancellationToken ct)
    {
        var map = new Dictionary<int, int>();
        if (testCaseIds.Count == 0) return map;

        var relations = await QueryLinksAsync(testCaseIds, parentDirection: true, ct);
        var targets = relations.Where(r => r.Target is not null).Select(r => r.Target!.Id).Distinct().ToList();
        if (targets.Count == 0) return map;

        var features = (await HydrateDtosAsync(targets, RefFields, ct))
            .Where(d => IsType(d, "Feature"))
            .Select(d => d.Id)
            .ToHashSet();

        foreach (var r in relations)
            if (r.Source is not null && r.Target is not null &&
                testCaseIds.Contains(r.Source.Id) && features.Contains(r.Target.Id))
                map[r.Source.Id] = r.Target.Id;
        return map;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetChildTestCasesAsync(
        IReadOnlyCollection<int> featureIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int>>();
        if (featureIds.Count == 0)
            return ToReadOnly(result);

        var relations = await QueryLinksAsync(featureIds, parentDirection: false, ct);
        var targets = relations.Where(r => r.Target is not null).Select(r => r.Target!.Id).Distinct().ToList();
        var testCases = (await HydrateDtosAsync(targets, RefFields, ct))
            .Where(d => IsType(d, "Test Case"))
            .Select(d => d.Id)
            .ToHashSet();

        foreach (var r in relations)
        {
            if (r.Source is null || r.Target is null) continue;
            if (!featureIds.Contains(r.Source.Id) || !testCases.Contains(r.Target.Id)) continue;
            if (!result.TryGetValue(r.Source.Id, out var list)) { list = []; result[r.Source.Id] = list; }
            list.Add(r.Target.Id);
        }
        return ToReadOnly(result);
    }

    public async IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(
        string workItemType, DateTimeOffset since, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        var type = WiqlEscaper.EscapeLiteral(workItemType);
        var sinceStr = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var wiql =
            $"SELECT [System.Id] FROM WorkItems WHERE [System.WorkItemType] = '{type}' " +
            $"AND [System.ChangedDate] >= '{sinceStr}' ORDER BY [System.ChangedDate] ASC";

        var ids = await QueryIdsAsync(wiql, 20000, ct);
        foreach (var chunk in ids.Chunk(Math.Max(1, pageSize)))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var dto in await HydrateDtosAsync(chunk, ChangeFields, ct))
                yield return ToRef(dto);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AdoWorkItemDto>> HydrateDtosAsync(
        IReadOnlyCollection<int> ids, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var results = new List<AdoWorkItemDto>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(_options.Ado.BatchSize))
        {
            var path = _client.OrgApiPath("wit/workitemsbatch?api-version=7.1");
            var resp = await _client.PostAsync<AdoListResponse<AdoWorkItemDto>>(path, new { ids = chunk, fields }, ct);
            results.AddRange(resp.Value);
        }
        return results;
    }

    private async Task<IReadOnlyList<WiqlRelation>> QueryLinksAsync(
        IReadOnlyCollection<int> sourceIds, bool parentDirection, CancellationToken ct)
    {
        var idList = string.Join(",", sourceIds.Distinct());
        var linkTypes = parentDirection
            ? "'System.LinkTypes.Hierarchy-Reverse','Microsoft.VSTS.Common.TestedBy-Reverse'"
            : "'System.LinkTypes.Hierarchy-Forward','Microsoft.VSTS.Common.TestedBy-Forward'";
        var wiql =
            $"SELECT [System.Id] FROM WorkItemLinks WHERE [Source].[System.Id] IN ({idList}) " +
            $"AND [System.Links.LinkType] IN ({linkTypes}) MODE (MustContain)";
        var path = _client.ProjectApiPath("wit/wiql?api-version=7.1");
        var resp = await _client.PostAsync<WiqlResponse>(path, new { query = wiql }, ct);
        return resp.WorkItemRelations;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> ToReadOnly(Dictionary<int, List<int>> map)
        => map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);

    private static AdoWorkItemRef ToRef(AdoWorkItemDto d) => new(
        d.Id, Str(d, "System.WorkItemType") ?? "", Str(d, "System.Title") ?? "",
        Str(d, "System.AreaPath"), Str(d, "System.State"), IntOr(d, "System.Rev", 0));

    private static bool IsType(AdoWorkItemDto d, string type)
        => string.Equals(Str(d, "System.WorkItemType"), type, StringComparison.OrdinalIgnoreCase);

    private static string? Str(AdoWorkItemDto d, string key)
        => d.Fields.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int IntOr(AdoWorkItemDto d, string key, int fallback)
        => d.Fields.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : fallback;

    private static int? IntOrNull(AdoWorkItemDto d, string key)
        => d.Fields.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static IReadOnlyList<string> SplitTags(string? tags)
        => string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? StripHtml(string? html)
        => string.IsNullOrWhiteSpace(html) ? html : StepsFlattener.StripTags(html);

    // WIQL response shapes (file-private).
    private sealed class WiqlResponse
    {
        [JsonPropertyName("workItems")] public List<WiqlRef> WorkItems { get; set; } = [];
        [JsonPropertyName("workItemRelations")] public List<WiqlRelation> WorkItemRelations { get; set; } = [];
    }

    private sealed class WiqlRef
    {
        [JsonPropertyName("id")] public int Id { get; set; }
    }

    private sealed class WiqlRelation
    {
        [JsonPropertyName("rel")] public string? Rel { get; set; }
        [JsonPropertyName("source")] public WiqlRef? Source { get; set; }
        [JsonPropertyName("target")] public WiqlRef? Target { get; set; }
    }
}
