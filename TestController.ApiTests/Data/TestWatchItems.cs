using System.Text.Json;
using TestController.ApiTests.Infrastructure;

namespace TestController.ApiTests.Data;

/// <summary>
/// Source of WatchItem tags for tests. Hardcoding a tag is fragile because the
/// active WatchList differs between InMemory (seeded "TestBuild") and a Live server
/// (e.g. "ConsolidatedBuild"). Prefer <see cref="DiscoverEnabledTagAsync"/>, which
/// asks the running server for a real, enabled tag.
/// </summary>
public static class TestWatchItems
{
    /// <summary>The enabled tag seeded by ApiTestWebFactory in InMemory mode.</summary>
    public const string InMemoryEnabledTag = "TestBuild";

    /// <summary>
    /// Returns the tag of the first enabled WatchItem reported by GET /api/watchlist.
    /// Falls back to <see cref="InMemoryEnabledTag"/> if none can be determined.
    /// </summary>
    public static async Task<string> DiscoverEnabledTagAsync(ApiClient api)
    {
        var root = await api.GetWatchListAsync();
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("watchItems", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var enabled = !item.TryGetProperty("isEnabled", out var en) ||
                              en.ValueKind != JsonValueKind.False;
                if (!enabled) continue;
                if (item.TryGetProperty("tag", out var tag) &&
                    tag.ValueKind == JsonValueKind.String)
                {
                    var value = tag.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value!;
                }
            }
        }
        return InMemoryEnabledTag;
    }
}
