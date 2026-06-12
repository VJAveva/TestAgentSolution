using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestController.Api.Contracts;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// ViewModel for the Lock Conflict Dialog (Mockup 6).
/// Shows owner details, expiry countdown, and force-release strip (if permitted).
/// </summary>
public sealed partial class LockConflictDialogViewModel : ObservableObject, IDisposable
{
    private readonly CapabilityChecker _capabilityChecker;
    private DispatcherTimer? _countdownTimer;

    [ObservableProperty] private string _pipelineId = "";
    [ObservableProperty] private string _ownerDisplayName = "";
    [ObservableProperty] private string _ownerClientKind = "";
    [ObservableProperty] private string _ownerInitials = "";
    [ObservableProperty] private string _acquiredAtText = "";
    [ObservableProperty] private string _expiryCountdownText = "";
    [ObservableProperty] private bool _canForceRelease;
    [ObservableProperty] private bool _requestClose;

    private DateTime _expiresUtc;

    public LockConflictDialogViewModel(CapabilityChecker capabilityChecker)
    {
        _capabilityChecker = capabilityChecker;
        _capabilityChecker.CapabilitiesChanged += OnCapabilitiesChanged;
        RefreshForceReleaseVisibility();
    }

    /// <summary>Initialize from the conflict DTO — no refetch needed.</summary>
    public void Initialize(PipelineLockDto dto)
    {
        PipelineId = dto.PipelineId;
        OwnerDisplayName = dto.OwnerDisplayName;
        OwnerClientKind = dto.OwnerClientKind;
        OwnerInitials = GetInitials(dto.OwnerDisplayName);
        AcquiredAtText = dto.AcquiredUtc.ToLocalTime().ToString("HH:mm:ss");
        _expiresUtc = dto.ExpiresUtc;

        UpdateCountdown();
        StartCountdownTimer();
    }

    /// <summary>Raised when the user wants to open the force-release reason dialog.</summary>
    public event Action? ForceReleaseRequested;

    [RelayCommand]
    private void Close() => RequestClose = true;

    [RelayCommand]
    private void ViewDashboard()
    {
        // Navigate to the dashboard tab for this pipeline — handled by the dialog host
        RequestClose = true;
    }

    [RelayCommand]
    private void ForceRelease()
    {
        ForceReleaseRequested?.Invoke();
    }

    private void RefreshForceReleaseVisibility()
    {
        CanForceRelease = _capabilityChecker.Can(Permission.Pipeline_ForceRelease, PipelineId);
    }

    private void OnCapabilitiesChanged()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshForceReleaseVisibility);
    }

    private void StartCountdownTimer()
    {
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();
    }

    private void UpdateCountdown()
    {
        var remaining = _expiresUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ExpiryCountdownText = "Expired";
            _countdownTimer?.Stop();
        }
        else
        {
            ExpiryCountdownText = $"Expires in {(int)remaining.TotalSeconds}s";
        }
    }

    private static string GetInitials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "?";
        var parts = displayName.Split('.', ' ', '_');
        if (parts.Length >= 2)
            return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
        return displayName[..1].ToUpper();
    }

    public void Dispose()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
        _capabilityChecker.CapabilitiesChanged -= OnCapabilitiesChanged;
    }
}
