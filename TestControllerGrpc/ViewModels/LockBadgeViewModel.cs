using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TestController.Api.Contracts;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Per-row lock badge state. Resolved from LockStateService lock map.
/// Shows "Your run · mm:ss" (blue) or "Locked by {name} ({client})" (amber).
/// </summary>
public sealed partial class LockBadgeViewModel : ObservableObject, IDisposable
{
    private readonly LockStateService _lockStateService;
    private readonly CurrentUserHolder _currentUserHolder;
    private readonly string _tag;
    private DispatcherTimer? _elapsedTimer;
    private DateTime _lockAcquiredUtc;

    [ObservableProperty] private bool _isLockedByMe;
    [ObservableProperty] private bool _isLockedByOther;
    [ObservableProperty] private string _ownerLabel = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private bool _isVisible;

    public LockBadgeViewModel(LockStateService lockStateService, CurrentUserHolder currentUserHolder, string tag)
    {
        _lockStateService = lockStateService;
        _currentUserHolder = currentUserHolder;
        _tag = tag;

        _lockStateService.LocksChanged += OnLocksChanged;
        Refresh();
    }

    /// <summary>Protected constructor for design-time / tests.</summary>
    internal LockBadgeViewModel() { _lockStateService = null!; _currentUserHolder = null!; _tag = ""; }

    public void Refresh()
    {
        var dto = _lockStateService.GetLock(_tag);
        if (dto is null)
        {
            IsLockedByMe = false;
            IsLockedByOther = false;
            IsVisible = false;
            OwnerLabel = "";
            ElapsedText = "";
            StopTimer();
            return;
        }

        var lockedByOther = _lockStateService.IsLockedByOther(_tag);
        IsLockedByOther = lockedByOther;
        IsLockedByMe = !lockedByOther;
        IsVisible = IsLockedByOther || IsLockedByMe;

        if (IsLockedByOther)
        {
            OwnerLabel = $"Locked by {dto.OwnerDisplayName} ({dto.OwnerClientKind})";
            StopTimer();
        }
        else if (IsLockedByMe)
        {
            _lockAcquiredUtc = dto.AcquiredUtc;
            UpdateElapsedText();
            StartTimer();
        }
    }

    private void OnLocksChanged() => Refresh();

    private void StartTimer()
    {
        if (_elapsedTimer is not null) return;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();
        _elapsedTimer.Start();
    }

    private void StopTimer()
    {
        if (_elapsedTimer is null) return;
        _elapsedTimer.Stop();
        _elapsedTimer = null;
    }

    private void UpdateElapsedText()
    {
        var elapsed = DateTime.UtcNow - _lockAcquiredUtc;
        ElapsedText = $"Your run \u00B7 {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    public void Dispose()
    {
        StopTimer();
        if (_lockStateService is not null)
            _lockStateService.LocksChanged -= OnLocksChanged;
    }
}
