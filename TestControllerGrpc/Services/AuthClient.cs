using System.Net.Http;
using System.Net.Http.Json;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Services;

/// <summary>
/// WPF-side auth service. Calls REST /api/auth/* endpoints.
/// Stores session token in-memory only (not persisted on disk).
/// Singleton DI lifetime.
/// </summary>
public class AuthClient
{
    private readonly HttpClient _http;
    private readonly IAppLogger _logger;

    private string? _token;
    private AuthUserInfo? _currentUser;

    /// <summary>Current session token. Null when not logged in.</summary>
    public string? Token => _token;

    /// <summary>Currently authenticated user info. Null when not logged in.</summary>
    public AuthUserInfo? CurrentUser => _currentUser;

    /// <summary>True if a valid session is active.</summary>
    public bool IsAuthenticated => _token is not null;

    /// <summary>Raised after login/logout changes the current user.</summary>
    public event Action? AuthStateChanged;

    public AuthClient(IHttpClientFactory httpClientFactory, IAppLogger logger)
    {
        _http = httpClientFactory.CreateClient("SystemMode");
        _logger = logger;
    }

    /// <summary>Protected constructor for test fakes.</summary>
    protected AuthClient()
    {
        _http = null!;
        _logger = null!;
    }

    public virtual async Task<LoginResult> LoginAsync(string username, string password)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/login", new
            {
                username,
                password,
            });

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                return new LoginResult(false, Error: err?.Error ?? "Login failed");
            }

            var data = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
            if (data is null)
                return new LoginResult(false, Error: "Invalid response");

            _token = data.Token;
            _logger.Info("Auth", $"Login successful for {username}, role={data.Role}");

            // Fetch full user info
            await FetchMeAsync();

            return new LoginResult(true, MustChangePassword: data.MustChangePassword);
        }
        catch (Exception ex)
        {
            _logger.Error("Auth", "Login failed", ex);
            return new LoginResult(false, Error: "Connection failed");
        }
    }

    public virtual async Task LogoutAsync()
    {
        if (_token is null) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.Warn("Auth", $"Logout request failed (best-effort): {ex.Message}");
        }
        finally
        {
            _token = null;
            _currentUser = null;
            AuthStateChanged?.Invoke();
            _logger.Info("Auth", "Logged out, session cleared");
        }
    }

    public virtual async Task<(bool Success, string? Error)> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (_token is null)
            return (false, "Not authenticated");

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
            {
                Content = JsonContent.Create(new { currentPassword, newPassword }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                return (false, err?.Error ?? "Password change failed");
            }

            // Token was revoked server-side after password change; clear local state
            _token = null;
            _currentUser = null;
            AuthStateChanged?.Invoke();

            _logger.Info("Auth", "Password changed successfully, session cleared");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Auth", "ChangePassword failed", ex);
            return (false, "Connection failed");
        }
    }

    public async Task FetchMeAsync()
    {
        if (_token is null) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _currentUser = await response.Content.ReadFromJsonAsync<AuthUserInfo>();
                AuthStateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Auth", $"FetchMe failed: {ex.Message}");
        }
    }

    public sealed record LoginResult(bool Success, bool MustChangePassword = false, string? Error = null);
    private sealed record LoginResponseDto(string Token, string Role, bool MustChangePassword);
    private sealed record ErrorResponse(string? Error);
}

/// <summary>User info returned by /api/auth/me.</summary>
public sealed record AuthUserInfo(
    string UserId,
    string Username,
    string DisplayName,
    string Role,
    string ClientKind,
    List<string> Capabilities,
    bool MustChangePassword,
    bool IsGuest,
    List<string> AssignedPipelineIds);
