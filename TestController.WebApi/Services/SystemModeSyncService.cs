using System.Text.Json;
using Microsoft.Extensions.Options;
using TestController.Api.Hubs;
using TestControllerGrpc.Configuration;

namespace TestController.WebApi.Services;

/// <summary>
/// Keeps the standalone WebApi's RBAC mode in sync with the WPF controller.
///
/// The WebApi is a SECONDARY host: it has its own <see cref="RbacOptions"/> flag and its
/// own SignalR hub, neither of which sees mode switches performed on the controller
/// (which owns the database and is the source of truth). Without this relay, switching
/// to Secured mode in the WPF app leaves the IIS-hosted web client stuck in Default mode.
///
/// This service polls the controller's <c>/api/system/mode</c> endpoint and, when the
/// controller's mode differs from the WebApi's current flag, updates the WebApi's
/// in-memory <see cref="RbacOptions"/> (so <c>GET /api/system/mode</c> reports the truth)
/// and broadcasts <c>SystemModeChanged</c> to its own browser clients (so they reload).
///
/// The flag is updated via <see cref="IOptionsMonitorCache{TOptions}"/> (in-memory only) —
/// the WebApi runs under IIS where its appsettings.json is not writable by the app pool
/// identity, so the disk-backed <c>IWritableOptions</c> path used by the primary host
/// cannot be used here.
/// </summary>
public sealed class SystemModeSyncService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ControllerProxyService _proxy;
    private readonly IOptionsMonitor<RbacOptions> _options;
    private readonly IOptionsMonitorCache<RbacOptions> _optionsCache;
    private readonly SystemModeBroadcaster _broadcaster;
    private readonly ILogger<SystemModeSyncService> _logger;

    public SystemModeSyncService(
        ControllerProxyService proxy,
        IOptionsMonitor<RbacOptions> options,
        IOptionsMonitorCache<RbacOptions> optionsCache,
        SystemModeBroadcaster broadcaster,
        ILogger<SystemModeSyncService> logger)
    {
        _proxy = proxy;
        _options = options;
        _optionsCache = optionsCache;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_proxy.IsConfigured)
        {
            _logger.LogInformation(
                "SystemModeSyncService disabled: no ControllerProxyUrl configured.");
            return;
        }

        // Track the last value we applied so we only update/broadcast on an actual change.
        var lastKnownEnabled = _options.CurrentValue.Enabled;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                lastKnownEnabled = await SyncOnceAsync(lastKnownEnabled, ct);
                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Polls the controller once and, on change, updates the local flag and notifies clients.
    /// Never throws — a failure here must not fault the host (BackgroundService StopHost behavior).
    /// Returns the mode that is now in effect locally.
    /// </summary>
    private async Task<bool> SyncOnceAsync(bool lastKnownEnabled, CancellationToken ct)
    {
        try
        {
            var controllerEnabled = await GetControllerEnabledAsync(ct);
            if (controllerEnabled is null || controllerEnabled.Value == lastKnownEnabled)
                return lastKnownEnabled;

            var enabled = controllerEnabled.Value;

            // Override the in-memory options value so SystemModeController.GetMode reports it.
            _optionsCache.TryRemove(Options.DefaultName);
            _optionsCache.TryAdd(Options.DefaultName, new RbacOptions { Enabled = enabled });

            var mode = enabled ? "secured" : "default";
            _logger.LogInformation(
                "System mode changed on controller -> {Mode}; syncing WebApi and notifying clients.", mode);
            await _broadcaster.BroadcastModeChangedAsync(mode);

            return enabled;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("System mode sync attempt failed: {Message}", ex.Message);
            return lastKnownEnabled;
        }
    }

    private async Task<bool?> GetControllerEnabledAsync(CancellationToken ct)
    {
        var response = await _proxy.ForwardGetAsync("/api/system/mode", authorizationHeader: null);
        if (response is null || !response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("enabled", out var enabledProp)
            && (enabledProp.ValueKind == JsonValueKind.True || enabledProp.ValueKind == JsonValueKind.False))
        {
            return enabledProp.GetBoolean();
        }

        return null;
    }
}
