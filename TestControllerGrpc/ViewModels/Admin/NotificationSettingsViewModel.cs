using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// ViewModel for notification settings and mute management (Phase 8).
/// Read-only display of settings + mute list management.
/// CommunityToolkit.Mvvm, Singleton DI.
/// </summary>
public sealed partial class NotificationSettingsViewModel : ObservableObject
{
    private readonly NotificationMuteClient _muteClient;
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly CapabilityChecker _capabilityChecker;
    private readonly IAppLogger _logger;

    // ── Settings (read-only display) ────────────────────────────────────
    [ObservableProperty] private bool _autoAlertEnabled;
    [ObservableProperty] private bool _alertOnThresholdOnly;
    [ObservableProperty] private int _cooldownHours;

    // ── Mute list ───────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<MuteItemDto> _mutes = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _canMute;

    public NotificationSettingsViewModel(
        NotificationMuteClient muteClient,
        IOptionsMonitor<NotificationOptions> options,
        CapabilityChecker capabilityChecker,
        IAppLogger logger)
    {
        _muteClient = muteClient;
        _options = options;
        _capabilityChecker = capabilityChecker;
        _logger = logger;

        RefreshSettings();
        _capabilityChecker.CapabilitiesChanged += () =>
            CanMute = _capabilityChecker.Can(Permission.Notification_Mute);
        CanMute = _capabilityChecker.Can(Permission.Notification_Mute);
    }

    private void RefreshSettings()
    {
        var opts = _options.CurrentValue;
        AutoAlertEnabled = opts.AutoAlertEnabled;
        AlertOnThresholdOnly = opts.AlertOnThresholdOnly;
        CooldownHours = opts.CooldownHours;
    }

    [RelayCommand]
    private async Task LoadMutesAsync()
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var items = await _muteClient.ListMutesAsync();
            Mutes = new ObservableCollection<MuteItemDto>(items);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to load mutes";
            _logger.Error("NotificationSettings", "LoadMutes failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UnmuteAsync(long muteId)
    {
        var success = await _muteClient.UnmuteAsync(muteId);
        if (success)
            await LoadMutesAsync();
    }
}
