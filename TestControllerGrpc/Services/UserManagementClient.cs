using System.Net.Http;
using System.Net.Http.Json;

namespace TestControllerGrpc.Services;

/// <summary>
/// WPF-side HTTP client for user management endpoints.
/// Singleton — uses IHttpClientFactory named "SystemMode" (same base URL).
/// </summary>
public class UserManagementClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _authClient;
    private readonly IAppLogger _logger;

    public UserManagementClient(IHttpClientFactory httpClientFactory, AuthClient authClient, IAppLogger logger)
    {
        _http = httpClientFactory.CreateClient("SystemMode");
        _authClient = authClient;
        _logger = logger;
    }

    /// <summary>Protected constructor for test fakes.</summary>
    protected UserManagementClient()
    {
        _http = null!;
        _authClient = null!;
        _logger = null!;
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_authClient.Token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authClient.Token);
        return request;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    public sealed record UserDto(
        string UserId, string Username, string Email, string Role,
        bool IsActive, bool MustChangePassword, DateTime CreatedUtc,
        string? CreatedByUserId, int PipelineCount);

    public sealed record CreateUserResponse(UserDto? User, string? GeneratedPassword, string? Error);
    public sealed record AssignmentsResponse(List<string> PipelineIds);

    // ── List / Get ───────────────────────────────────────────────────────

    public virtual async Task<List<UserDto>> ListUsersAsync(string? usernameFilter = null)
    {
        var path = string.IsNullOrWhiteSpace(usernameFilter)
            ? "/api/users"
            : $"/api/users?username={Uri.EscapeDataString(usernameFilter)}";

        var request = AuthorizedRequest(HttpMethod.Get, path);
        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<UserDto>>() ?? [];
    }

    public virtual async Task<UserDto?> GetUserAsync(string userId)
    {
        var request = AuthorizedRequest(HttpMethod.Get, $"/api/users/{userId}");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<UserDto>();
    }

    // ── Create ───────────────────────────────────────────────────────────

    public virtual async Task<(UserDto? User, string? GeneratedPassword, string? Error)> CreateUserAsync(
        string username, string email, string role, string? password = null)
    {
        var request = AuthorizedRequest(HttpMethod.Post, "/api/users");
        request.Content = JsonContent.Create(new { username, email, role, password });

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadFromJsonAsync<ErrorBody>();
            return (null, null, errBody?.Error ?? response.ReasonPhrase);
        }

        var result = await response.Content.ReadFromJsonAsync<CreateUserResponse>();
        return (result?.User, result?.GeneratedPassword, null);
    }

    // ── Update ───────────────────────────────────────────────────────────

    public virtual async Task<(bool Success, string? Error)> UpdateUserAsync(string userId, string? email, string? role)
    {
        var request = AuthorizedRequest(HttpMethod.Put, $"/api/users/{userId}");
        request.Content = JsonContent.Create(new { email, role });

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadFromJsonAsync<ErrorBody>();
            return (false, errBody?.Error ?? response.ReasonPhrase);
        }
        return (true, null);
    }

    // ── Delete ───────────────────────────────────────────────────────────

    public virtual async Task<(bool Success, string? Error)> DeleteUserAsync(string userId)
    {
        var request = AuthorizedRequest(HttpMethod.Delete, $"/api/users/{userId}");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadFromJsonAsync<ErrorBody>();
            return (false, errBody?.Error ?? response.ReasonPhrase);
        }
        return (true, null);
    }

    // ── Assignments ──────────────────────────────────────────────────────

    public virtual async Task<List<string>> GetAssignmentsAsync(string userId)
    {
        var request = AuthorizedRequest(HttpMethod.Get, $"/api/users/{userId}/assignments");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return [];
        var result = await response.Content.ReadFromJsonAsync<AssignmentsResponse>();
        return result?.PipelineIds ?? [];
    }

    public virtual async Task<(bool Success, string? Error)> SetAssignmentsAsync(string userId, List<string> pipelineIds)
    {
        var request = AuthorizedRequest(HttpMethod.Post, $"/api/users/{userId}/assign-pipelines");
        request.Content = JsonContent.Create(new { pipelineIds });

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadFromJsonAsync<ErrorBody>();
            return (false, errBody?.Error ?? response.ReasonPhrase);
        }
        return (true, null);
    }

    // ── Reset Password ───────────────────────────────────────────────────

    public virtual async Task<(string? NewPassword, string? Error)> ResetPasswordAsync(string userId)
    {
        var request = AuthorizedRequest(HttpMethod.Post, $"/api/users/{userId}/reset-password");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadFromJsonAsync<ErrorBody>();
            return (null, errBody?.Error ?? response.ReasonPhrase);
        }
        var result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        return (result?.NewPassword, null);
    }

    // ── Username check ───────────────────────────────────────────────────

    public virtual async Task<bool> CheckUsernameExistsAsync(string username)
    {
        var request = AuthorizedRequest(HttpMethod.Get, $"/api/users/check-username?username={Uri.EscapeDataString(username)}");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;
        var result = await response.Content.ReadFromJsonAsync<UsernameCheckResponse>();
        return result?.Exists ?? false;
    }

    private sealed record ErrorBody(string? Error);
    private sealed record ResetPasswordResponse(string? NewPassword);
    private sealed record UsernameCheckResponse(bool Exists);
}
