using System.Net.Http.Json;
using System.Text.Json;

namespace TestController.WebApi.Services;

/// <summary>
/// Proxies dashboard requests to the WPF controller's embedded API when the
/// standalone WebApi is running alongside the WPF controller on the same
/// machine. This bridges the gap where executions triggered from the WPF app
/// are invisible to the standalone WebApi's own ExecutionSessionManager.
///
/// Configure via appsettings: <c>"ControllerProxyUrl": "http://localhost:5200"</c>
/// </summary>
public sealed class ControllerProxyService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<ControllerProxyService> _logger;
    private readonly string? _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ControllerProxyService(IConfiguration config, ILogger<ControllerProxyService> logger)
    {
        _logger = logger;
        _baseUrl = config["ControllerProxyUrl"];

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Whether a WPF controller proxy URL is configured.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

    /// <summary>
    /// Fetches dashboard session data from the WPF controller's embedded API.
    /// Returns null on any failure (timeout, unreachable, bad response) so the
    /// caller can fall back to local data gracefully.
    /// </summary>
    public async Task<DashboardSessionsResponse?> GetDashboardSessionsAsync()
    {
        if (!IsConfigured) return null;

        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/execution/dashboard-sessions");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Controller proxy returned {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<DashboardSessionsResponse>(JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("Controller proxy unavailable: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fetches execution status from the WPF controller.
    /// </summary>
    public async Task<ExecutionStatusResponse?> GetExecutionStatusAsync()
    {
        if (!IsConfigured) return null;

        try
        {
            return await _http.GetFromJsonAsync<ExecutionStatusResponse>(
                $"{_baseUrl}/api/execution/status", JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("Controller proxy status unavailable: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fetches active sessions list from the WPF controller.
    /// </summary>
    public async Task<SessionsListResponse?> GetSessionsAsync()
    {
        if (!IsConfigured) return null;

        try
        {
            return await _http.GetFromJsonAsync<SessionsListResponse>(
                $"{_baseUrl}/api/execution/sessions", JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("Controller proxy sessions unavailable: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fetches recent log entries for a specific session from the WPF controller.
    /// </summary>
    public async Task<JsonElement?> GetRecentLogsAsync(string sessionId, int count = 200)
    {
        if (!IsConfigured) return null;

        try
        {
            var response = await _http.GetAsync(
                $"{_baseUrl}/api/execution/{Uri.EscapeDataString(sessionId)}/recent-logs?count={count}");
            if (!response.IsSuccessStatusCode) return null;

            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug("Controller proxy logs unavailable: {Message}", ex.Message);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

// Response DTOs for deserialization
public record DashboardSessionsResponse
{
    public List<JsonElement> Active { get; init; } = [];
    public List<JsonElement> History { get; init; } = [];
}

public record ExecutionStatusResponse
{
    public bool IsExecuting { get; init; }
    public int ActiveCount { get; init; }
}

public record SessionsListResponse
{
    public int ActiveCount { get; init; }
    public bool HasActive { get; init; }
    public List<JsonElement> Sessions { get; init; } = [];
}
