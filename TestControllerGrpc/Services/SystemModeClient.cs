using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Configuration;

namespace TestControllerGrpc.Services;

/// <summary>
/// WPF-side client that calls the SystemMode REST API and subscribes to
/// SystemModeChanged SignalR events for live mode reload.
/// Singleton — registered in DI.
/// </summary>
public class SystemModeClient : IDisposable
{
    private readonly HttpClient? _http;
    private readonly AuthClient? _authClient;
    private readonly IOptionsMonitor<RbacOptions>? _rbacOptions;
    private readonly IAppLogger? _logger;
    private HubConnection? _hubConnection;

    /// <summary>Raised when the server broadcasts a mode change.</summary>
    public event Action<string>? ModeChanged;

    public SystemModeClient(
        IHttpClientFactory httpClientFactory,
        AuthClient authClient,
        IOptionsMonitor<RbacOptions> rbacOptions,
        IAppLogger logger)
    {
        _http = httpClientFactory.CreateClient("SystemMode");
        _authClient = authClient;
        _rbacOptions = rbacOptions;
        _logger = logger;
    }

    /// <summary>Protected constructor for test fakes.</summary>
    protected SystemModeClient() { }

    public bool IsSecuredMode => _rbacOptions?.CurrentValue.Enabled ?? false;

    /// <summary>Checks whether any Administrator account exists (active or archived).</summary>
    public virtual async Task<bool> HasExistingAdminAsync()
    {
        try
        {
            var response = await _http!.GetFromJsonAsync<AdminExistsResponse>("/api/system/mode/admin-exists");
            return response?.Exists ?? false;
        }
        catch (Exception ex)
        {
            _logger?.Warn("RBAC", $"HasExistingAdminAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Mode transitions are gated server-side by System_ChangeMode. X-Source identifies this
    /// process as the trusted WPF host (the only caller admitted in Default mode) and the
    /// bearer token identifies the signed-in Administrator (required in Secured mode).
    /// </summary>
    private HttpRequestMessage ModeChangeRequest(string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("X-Source", "WPF");
        if (_authClient?.Token is { } token)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public virtual async Task<(bool Success, string? Error)> SwitchToSecuredAsync(
        string username, string email, string password)
    {
        var request = ModeChangeRequest("/api/system/mode/secured", JsonContent.Create(new
        {
            username,
            email,
            password,
        }));
        var response = await _http!.SendAsync(request);

        if (response.IsSuccessStatusCode)
            return (true, null);

        return (false, await ReadErrorAsync(response));
    }

    public virtual async Task<(bool Success, string? Error)> SwitchToDefaultAsync()
    {
        var response = await _http!.SendAsync(ModeChangeRequest("/api/system/mode/default"));

        if (response.IsSuccessStatusCode)
            return (true, null);

        return (false, await ReadErrorAsync(response));
    }

    /// <summary>Switch to Secured mode by reactivating existing users (no wizard needed).</summary>
    public virtual async Task<(bool Success, string? Error)> SwitchToSecuredReactivateAsync()
    {
        var response = await _http!.SendAsync(ModeChangeRequest("/api/system/mode/secured/reactivate"));

        if (response.IsSuccessStatusCode)
            return (true, null);

        return (false, await ReadErrorAsync(response));
    }

    /// <summary>
    /// Locally raise ModeChanged — used after a successful switch initiated by this
    /// process. The WPF host is both server and client so the SignalR echo doesn't
    /// reach the embedded client (ConnectSignalRAsync is not wired up in-process).
    /// </summary>
    public void NotifyLocalModeChange(string mode)
    {
        _logger?.Info("RBAC", $"NotifyLocalModeChange: {mode}");
        ModeChanged?.Invoke(mode);
    }

    private sealed record AdminExistsResponse(bool Exists);

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            // ProblemDetails shape (detail) or legacy shape (error)
            if (json.TryGetProperty("detail", out var detail) && detail.GetString() is { } d)
                return d;
            if (json.TryGetProperty("error", out var err) && err.GetString() is { } e)
                return e;
        }
        catch { /* fall through */ }
        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }

    /// <summary>
    /// Connect to the SignalR hub and subscribe to SystemModeChanged.
    /// Call once during app startup.
    /// </summary>
    public async Task ConnectSignalRAsync(string hubUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<SystemModeChangedPayload>("SystemModeChanged", payload =>
        {
            _logger?.Info("RBAC", $"SystemModeChanged received: {payload.Mode}");
            ModeChanged?.Invoke(payload.Mode);
        });

        _hubConnection.Reconnected += _ =>
        {
            _logger?.Info("RBAC", "SignalR reconnected — re-checking system mode");
            ModeChanged?.Invoke(IsSecuredMode ? "secured" : "default");
            return Task.CompletedTask;
        };

        try
        {
            await _hubConnection.StartAsync();
            _logger?.Info("RBAC", "Connected to SignalR hub for SystemMode events");
        }
        catch (Exception ex)
        {
            _logger?.Error("RBAC", "Failed to connect SignalR for SystemMode", ex);
        }
    }

    public void Dispose()
    {
        _hubConnection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record SystemModeChangedPayload(string Mode);
}
