using System.Net.Http;
using System.Net.Http.Json;

namespace TestControllerGrpc.Services;

/// <summary>
/// WPF-side HTTP client for notification mute endpoints.
/// Singleton. Per Phase 8.
/// </summary>
public class NotificationMuteClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _authClient;
    private readonly IAppLogger _logger;

    public NotificationMuteClient(IHttpClientFactory httpClientFactory, AuthClient authClient, IAppLogger logger)
    {
        _http = httpClientFactory.CreateClient("SystemMode");
        _authClient = authClient;
        _logger = logger;
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_authClient.Token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authClient.Token);
        return request;
    }

    public async Task<List<MuteItemDto>> ListMutesAsync()
    {
        try
        {
            using var request = AuthorizedRequest(HttpMethod.Get, "/api/notifications/mutes");
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<MuteItemDto>>() ?? [];
        }
        catch (Exception ex)
        {
            _logger.Error("MuteClient", "ListMutes failed", ex);
            return [];
        }
    }

    public async Task<bool> MuteAsync(string target, string targetType = "Pipeline")
    {
        try
        {
            using var request = AuthorizedRequest(HttpMethod.Post, "/api/notifications/mutes");
            request.Content = JsonContent.Create(new { target, targetType });
            using var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Error("MuteClient", "Mute failed", ex);
            return false;
        }
    }

    public async Task<bool> UnmuteAsync(long muteId)
    {
        try
        {
            using var request = AuthorizedRequest(HttpMethod.Delete, $"/api/notifications/mutes/{muteId}");
            using var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Error("MuteClient", "Unmute failed", ex);
            return false;
        }
    }
}

public sealed class MuteItemDto
{
    public long MuteId { get; set; }
    public string Target { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string MutedByUserId { get; set; } = "";
    public DateTime MutedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
