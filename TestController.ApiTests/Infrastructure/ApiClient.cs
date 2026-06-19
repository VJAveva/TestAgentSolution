using System.Text.Json;

namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// Thin typed wrapper over the WebApi REST surface. Every route here was verified
/// against the real controllers/endpoints — do not "fix" them to match the old spec.
/// Methods return the raw <see cref="HttpResponseMessage"/> where the test needs to
/// assert status codes, and typed helpers where the body is the point.
/// </summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public ApiClient(HttpClient http) => _http = http;

    // ---- Execution -------------------------------------------------------

    /// <summary>POST /api/execution/trigger/{tag}?eventType=...</summary>
    public Task<HttpResponseMessage> TriggerAsync(
        string tag, TriggerRequest? body = null, string? eventType = null)
    {
        var url = $"/api/execution/trigger/{Uri.EscapeDataString(tag)}";
        if (!string.IsNullOrEmpty(eventType))
            url += $"?eventType={Uri.EscapeDataString(eventType)}";
        return _http.PostAsJsonAsync(url, body ?? new TriggerRequest(), JsonOpts);
    }

    /// <summary>GET /api/execution/{sessionId} — null when 404.</summary>
    public async Task<SessionStatus?> GetSessionAsync(string sessionId)
    {
        var resp = await _http.GetAsync($"/api/execution/{Uri.EscapeDataString(sessionId)}");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionStatus>(JsonOpts);
    }

    /// <summary>GET /api/execution/sessions.</summary>
    public async Task<SessionsEnvelope> GetSessionsAsync()
    {
        var resp = await _http.GetAsync("/api/execution/sessions");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionsEnvelope>(JsonOpts))!;
    }

    /// <summary>GET /api/execution/status.</summary>
    public async Task<ExecutionStatus> GetStatusAsync()
    {
        var resp = await _http.GetAsync("/api/execution/status");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ExecutionStatus>(JsonOpts))!;
    }

    /// <summary>POST /api/execution/{sessionId}/cancel.</summary>
    public Task<HttpResponseMessage> CancelSessionAsync(string sessionId, string? userId = null)
    {
        var body = new TriggerRequest(UserId: userId);
        return _http.PostAsJsonAsync(
            $"/api/execution/{Uri.EscapeDataString(sessionId)}/cancel", body, JsonOpts);
    }

    // ---- Agents ----------------------------------------------------------

    /// <summary>GET /api/agents — bare JSON array.</summary>
    public async Task<List<AgentInfo>> GetAgentsAsync()
    {
        var resp = await _http.GetAsync("/api/agents");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<AgentInfo>>(JsonOpts)) ?? new();
    }

    // ---- WatchList -------------------------------------------------------

    /// <summary>GET /api/watchlist — returned raw so tests can discover real tags.</summary>
    public async Task<JsonElement> GetWatchListAsync()
    {
        var resp = await _http.GetAsync("/api/watchlist");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    // ---- Health ----------------------------------------------------------

    /// <summary>GET /healthz/live (anonymous).</summary>
    public Task<HttpResponseMessage> GetHealthAsync() => _http.GetAsync("/healthz/live");

    // ---- Helpers ---------------------------------------------------------

    /// <summary>Reads a typed body from a known-success response.</summary>
    public static async Task<T?> ReadAsync<T>(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<T>(JsonOpts);
}
