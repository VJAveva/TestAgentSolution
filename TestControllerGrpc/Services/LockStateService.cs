using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using Microsoft.AspNetCore.SignalR.Client;
using TestController.Api.Contracts;
using TestControllerGrpc.Configuration;

namespace TestControllerGrpc.Services;

/// <summary>
/// Subscribes to pipeline lock SignalR events and maintains a thread-safe lock map
/// keyed by WatchItem Tag. Raises LocksChanged on the Dispatcher thread.
/// Singleton DI lifetime — registered in ControllerLockExtensions.
/// </summary>
public class LockStateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly AuthClient _authClient;
    private readonly CurrentUserHolder _currentUserHolder;
    private readonly IAppLogger _logger;
    private HubConnection? _hubConnection;

    private readonly ConcurrentDictionary<string, PipelineLockDto> _locks = new();

    /// <summary>Raised on the UI thread whenever the lock map changes.</summary>
    public event Action? LocksChanged;

    /// <summary>Raised on the UI thread when pipeline assignments change (PermissionsChanged signal from server).</summary>
    public event Action? AssignmentsChanged;

    public LockStateService(
        IHttpClientFactory httpClientFactory,
        AuthClient authClient,
        CurrentUserHolder currentUserHolder,
        IAppLogger logger)
    {
        _http = httpClientFactory.CreateClient("SystemMode");
        _authClient = authClient;
        _currentUserHolder = currentUserHolder;
        _logger = logger;
    }

    /// <summary>Protected constructor for test fakes.</summary>
    protected LockStateService() { _http = null!; _authClient = null!; _currentUserHolder = null!; _logger = null!; }

    /// <summary>Protected constructor for test fakes with user context.</summary>
    protected LockStateService(CurrentUserHolder currentUserHolder)
    {
        _http = null!;
        _authClient = null!;
        _currentUserHolder = currentUserHolder;
        _logger = null!;
    }

    /// <summary>Returns the lock for the given pipeline tag, or null if not locked.</summary>
    public PipelineLockDto? GetLock(string tag) => _locks.TryGetValue(tag, out var dto) ? dto : null;

    /// <summary>Returns true if the pipeline is locked by a different user.</summary>
    public bool IsLockedByOther(string tag)
    {
        if (!_locks.TryGetValue(tag, out var dto)) return false;
        var currentUserId = _currentUserHolder.User.UserId;
        // Same owner (by display name for Default mode compat) → not "other"
        return !string.Equals(dto.OwnerDisplayName, _currentUserHolder.User.DisplayName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(currentUserId, GetOwnerUserIdHeuristic(dto), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if the pipeline is locked by the current user.</summary>
    public bool IsLockedByMe(string tag)
    {
        if (!_locks.TryGetValue(tag, out var dto)) return false;
        return !IsLockedByOther(tag);
    }

    /// <summary>Returns a snapshot of all current locks.</summary>
    public IReadOnlyDictionary<string, PipelineLockDto> GetAll() => _locks;

    /// <summary>Connect to SignalR and perform initial lock sync.</summary>
    public async Task ConnectAsync(string hubUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.UseDefaultCredentials = true;
                if (_authClient.Token is not null)
                    options.Headers["Authorization"] = $"Bearer {_authClient.Token}";
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<PipelineLockDto>("PipelineLockAcquired", OnLockAcquired);
        _hubConnection.On<PipelineLockDto>("PipelineLockReleased", OnLockReleased);
        _hubConnection.On<PipelineLockDto>("PipelineLockExpired", OnLockExpired);
        _hubConnection.On<PipelineLockDto, string>("PipelineLockStolen", OnLockStolen);
        _hubConnection.On<PipelineLockDto, string>("PipelineLockRewritten", OnLockRewritten);
        _hubConnection.On("PermissionsChanged", OnPermissionsChanged);

        _hubConnection.Reconnected += async _ => await ResyncLocksAsync();

        try
        {
            await _hubConnection.StartAsync();
            await ResyncLocksAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("LockState", "Failed to connect SignalR for lock events", ex);
        }
    }

    private async Task ResyncLocksAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/locks");
            if (_authClient.Token is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authClient.Token);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var locks = await response.Content.ReadFromJsonAsync<List<PipelineLockDto>>();
                _locks.Clear();
                if (locks is not null)
                {
                    foreach (var l in locks)
                        _locks[l.PipelineId] = l;
                }
                RaiseLocksChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("LockState", "Failed to resync locks from server", ex);
        }
    }

    private void OnLockAcquired(PipelineLockDto dto)
    {
        _locks[dto.PipelineId] = dto;
        RaiseLocksChanged();
    }

    private void OnLockReleased(PipelineLockDto dto)
    {
        _locks.TryRemove(dto.PipelineId, out _);
        RaiseLocksChanged();
    }

    private void OnLockExpired(PipelineLockDto dto)
    {
        _locks.TryRemove(dto.PipelineId, out _);
        RaiseLocksChanged();
    }

    private void OnLockStolen(PipelineLockDto dto, string priorOwnerDisplayName)
    {
        _locks[dto.PipelineId] = dto;
        RaiseLocksChanged();
    }

    private void OnLockRewritten(PipelineLockDto dto, string priorOwnerDisplayName)
    {
        _locks[dto.PipelineId] = dto;
        RaiseLocksChanged();
    }

    private void RaiseLocksChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.InvokeAsync(() => LocksChanged?.Invoke());
        }
        else
        {
            LocksChanged?.Invoke();
        }
    }

    private void OnPermissionsChanged()
    {
        _logger.Info("LockState", "PermissionsChanged received — signaling assignment refresh");
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.InvokeAsync(() => AssignmentsChanged?.Invoke());
        }
        else
        {
            AssignmentsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Heuristic: in Default mode the OwnerDisplayName is "Default user" for the synthetic user.
    /// We compare display names for identity matching.
    /// </summary>
    private static string GetOwnerUserIdHeuristic(PipelineLockDto dto)
    {
        // The DTO doesn't carry UserId directly — identity matching uses DisplayName comparison
        // in the public IsLockedByOther method. This placeholder returns empty to force
        // the DisplayName comparison path.
        return string.Empty;
    }

    public void Dispose()
    {
        if (_hubConnection is not null)
        {
            _ = _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }
}
