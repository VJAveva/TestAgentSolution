using System.Net;
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
         "Microsoft.VSTS.TCM.Steps", "Microsoft.VSTS.TCM.AutomationStatus", "System.Tags", "System.Parent",
         "Microsoft.VSTS.TCM.AutomatedTestName", "Microsoft.VSTS.TCM.AutomatedTestStorage"];
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
            SplitTags(Str(d, "System.Tags")),
            Str(d, "Microsoft.VSTS.TCM.AutomatedTestName"),
            Str(d, "Microsoft.VSTS.TCM.AutomatedTestStorage"))).ToList();
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

        // Walks the hierarchy to Ado.LinkWalkMaxDepth. A Test Case is routinely a grandchild of the linked
        // work item (Feature -> User Story -> Test Case); stopping at one level silently drops it, and these
        // are weight-1.0 anchors that bypass the selection budget entirely.
        int maxDepth = Math.Max(1, _options.Ado.LinkWalkMaxDepth);
        var seen = new HashSet<int>(featureIds);
        var frontier = featureIds.ToList();
        var descendants = new Dictionary<int, HashSet<int>>();
        var candidateTargets = new HashSet<int>();

        for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var relations = await QueryLinksAsync(frontier, parentDirection: false, ct);
            var next = new List<int>();

            foreach (var r in relations)
            {
                if (r.Source is null || r.Target is null) continue;

                // Attribute the target to every root that reaches this source, so a grandchild is credited
                // to the originally linked work item rather than to the intermediate node.
                IEnumerable<int> roots = descendants
                    .Where(kv => kv.Value.Contains(r.Source.Id))
                    .Select(kv => kv.Key)
                    .DefaultIfEmpty(r.Source.Id);

                foreach (int root in roots)
                {
                    if (!descendants.TryGetValue(root, out var set)) { set = []; descendants[root] = set; }
                    set.Add(r.Target.Id);
                }

                candidateTargets.Add(r.Target.Id);
                if (seen.Add(r.Target.Id)) next.Add(r.Target.Id);
            }

            frontier = next;
        }

        if (candidateTargets.Count == 0)
            return ToReadOnly(result);

        var testCases = (await HydrateDtosAsync(candidateTargets.ToList(), RefFields, ct))
            .Where(d => IsType(d, "Test Case"))
            .Select(d => d.Id)
            .ToHashSet();

        foreach (int featureId in featureIds)
        {
            if (!descendants.TryGetValue(featureId, out var reachable)) continue;

            var list = reachable.Where(testCases.Contains).ToList();
            if (list.Count > 0) result[featureId] = list;
        }

        return ToReadOnly(result);
    }

    // ADO WIQL is SQL Server-backed and rejects dates before 1753-01-01 with TF51586. A full rebuild passes
    // DateTimeOffset.MinValue (year 0001) to mean "everything"; clamp to a safe floor so the query is valid.
    private static readonly DateTimeOffset MinWiqlDate = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A flat WIQL query returns at most 20000 rows and throws VS402337 when it MATCHES more (independent of
    // $top). A full rebuild spans the whole corpus, so we walk [since, now) in adaptive date windows: an
    // overflowing window is halved and retried; a sparse window is doubled so empty history is skipped fast.
    // [System.ChangedDate] has DAY precision in WIQL (a time component is rejected), so windows are whole days
    // and boundaries are formatted date-only — halving stays integer so a boundary never lands mid-day.
    private const int WiqlRowCap = 20000;
    private const int InitialWindowDays = 180;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 3650;

    public async IAsyncEnumerable<AdoWorkItemRef> EnumerateChangedSinceAsync(
        string workItemType, DateTimeOffset since, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        var type = WiqlEscaper.EscapeLiteral(workItemType);
        var buffer = new List<int>(Math.Max(1, pageSize));

        await foreach (var id in EnumerateWindowedIdsAsync(type, since, ExcludedStateClause(), ct))
        {
            buffer.Add(id);
            if (buffer.Count < Math.Max(1, pageSize)) continue;

            foreach (var dto in await HydrateDtosAsync(buffer, ChangeFields, ct))
                yield return ToRef(dto);
            buffer.Clear();
        }

        if (buffer.Count == 0) yield break;
        foreach (var dto in await HydrateDtosAsync(buffer, ChangeFields, ct))
            yield return ToRef(dto);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<int> EnumerateIdsInStatesAsync(
        string workItemType, IReadOnlyCollection<string> states, DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (states.Count == 0) yield break;

        var type = WiqlEscaper.EscapeLiteral(workItemType);
        var clause = $"AND [System.State] IN ({StateList(states)}) ";
        await foreach (var id in EnumerateWindowedIdsAsync(type, since, clause, ct))
            yield return id;
    }

    private static string StateList(IEnumerable<string> states)
        => string.Join(", ", states.Select(s => $"'{WiqlEscaper.EscapeLiteral(s)}'"));

    // Excluding Removed/Closed at the source keeps deleted test cases out of the corpus entirely, so they can
    // never be retrieved or recommended. Empty config means no filter, matching the previous behaviour.
    private string ExcludedStateClause()
    {
        IReadOnlyList<string> states = _options.Ado.ExcludedStates;
        return states is null || states.Count == 0
            ? ""
            : $"AND [System.State] NOT IN ({StateList(states)}) ";
    }

    private async IAsyncEnumerable<int> EnumerateWindowedIdsAsync(
        string escapedType, DateTimeOffset since, string stateClause, [EnumeratorCancellation] CancellationToken ct)
    {
        var floor = since < MinWiqlDate ? MinWiqlDate : since;
        var cursor = new DateTimeOffset(floor.UtcDateTime.Date, TimeSpan.Zero);
        var upper = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        var windowDays = InitialWindowDays;

        while (cursor < upper)
        {
            ct.ThrowIfCancellationRequested();
            var end = cursor.AddDays(windowDays);
            if (end > upper) end = upper;

            IReadOnlyList<int> ids;
            try
            {
                ids = await QueryWindowIdsAsync(escapedType, cursor, end, stateClause, ct);
            }
            catch (AdoApiException ex) when (IsResultSizeExceeded(ex) && windowDays > MinWindowDays)
            {
                // Too many work items in this span — halve the window and retry the same start.
                windowDays = Math.Max(MinWindowDays, windowDays / 2);
                continue;
            }

            foreach (var id in ids)
                yield return id;

            cursor = end;
            // Grow the window when a span is sparse so empty history is skipped in a few queries.
            if (ids.Count < WiqlRowCap / 4 && windowDays < MaxWindowDays)
                windowDays = Math.Min(MaxWindowDays, windowDays * 2);
        }
    }

    private async Task<IReadOnlyList<int>> QueryWindowIdsAsync(
        string escapedType, DateTimeOffset startInclusive, DateTimeOffset endExclusive, string stateClause,
        CancellationToken ct)
    {
        var from = startInclusive.ToUniversalTime().ToString("yyyy-MM-dd");
        var to = endExclusive.ToUniversalTime().ToString("yyyy-MM-dd");
        var wiql =
            $"SELECT [System.Id] FROM WorkItems WHERE [System.WorkItemType] = '{escapedType}' " +
            $"AND [System.ChangedDate] >= '{from}' AND [System.ChangedDate] < '{to}' " +
            stateClause +
            $"ORDER BY [System.ChangedDate] ASC";
        return await QueryIdsAsync(wiql, WiqlRowCap, ct);
    }

    private static bool IsResultSizeExceeded(AdoApiException ex) =>
        ex.StatusCode == HttpStatusCode.BadRequest &&
        ex.Message.Contains("VS402337", StringComparison.OrdinalIgnoreCase);

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
