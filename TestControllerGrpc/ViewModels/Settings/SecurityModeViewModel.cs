using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Configuration;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Settings;

/// <summary>
/// ViewModel for the Security Mode settings panel.
/// CommunityToolkit.Mvvm source-generated properties and commands.
/// </summary>
public sealed partial class SecurityModeViewModel : ObservableObject
{
    private readonly IOptionsMonitor<RbacOptions> _rbacOptions;
    private readonly SystemModeClient _systemModeClient;
    private readonly IAppLogger _logger;

    [ObservableProperty] private bool _isSecuredMode;
    [ObservableProperty] private bool _isSwitching;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Raised when the wizard/dialog should close. Bool = success.</summary>
    public event Action<bool>? RequestClose;

    // Initial Admin Wizard fields
    [ObservableProperty] private bool _isWizardOpen;
    [ObservableProperty] private string _wizardUsername = "";
    [ObservableProperty] private string _wizardEmail = "";
    [ObservableProperty] private string _wizardPassword = "";
    [ObservableProperty] private string _wizardConfirmPassword = "";
    [ObservableProperty] private string _wizardError = "";

    // Reactivation confirmation fields (shown when admin already exists)
    [ObservableProperty] private bool _isReactivateDialogOpen;

    // Disable RBAC confirmation fields
    [ObservableProperty] private bool _isDisableDialogOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSwitchToDefaultCommand))]
    private string _disableConfirmText = "";

    [ObservableProperty] private string _disableError = "";

    private const string DisableConfirmString = "DISABLE RBAC";

    public SecurityModeViewModel(
        IOptionsMonitor<RbacOptions> rbacOptions,
        SystemModeClient systemModeClient,
        IAppLogger logger)
    {
        _rbacOptions = rbacOptions;
        _systemModeClient = systemModeClient;
        _logger = logger;

        IsSecuredMode = _rbacOptions.CurrentValue.Enabled;

        _rbacOptions.OnChange(opts =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => IsSecuredMode = opts.Enabled);
        });
    }

    public bool IsDefaultMode => !IsSecuredMode;

    partial void OnIsSecuredModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDefaultMode));
    }

    public bool CanConfirmDisable => string.Equals(DisableConfirmText?.Trim(), DisableConfirmString, StringComparison.Ordinal);

    partial void OnDisableConfirmTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanConfirmDisable));
    }

    public bool IsWizardValid =>
        !string.IsNullOrWhiteSpace(WizardUsername) &&
        !string.IsNullOrWhiteSpace(WizardEmail) &&
        !string.IsNullOrWhiteSpace(WizardPassword) &&
        WizardPassword.Length >= 12 &&
        WizardPassword == WizardConfirmPassword;

    partial void OnWizardUsernameChanged(string value) => OnPropertyChanged(nameof(IsWizardValid));
    partial void OnWizardEmailChanged(string value) => OnPropertyChanged(nameof(IsWizardValid));
    partial void OnWizardPasswordChanged(string value) => OnPropertyChanged(nameof(IsWizardValid));
    partial void OnWizardConfirmPasswordChanged(string value) => OnPropertyChanged(nameof(IsWizardValid));

    [RelayCommand]
    private async Task OpenWizardAsync()
    {
        // Check if an admin already exists — if so, skip wizard and show reactivation confirmation
        var adminExists = await _systemModeClient.HasExistingAdminAsync();
        if (adminExists)
        {
            IsReactivateDialogOpen = true;
            return;
        }

        WizardUsername = "";
        WizardEmail = "";
        WizardPassword = "";
        WizardConfirmPassword = "";
        WizardError = "";
        IsWizardOpen = true;
    }

    [RelayCommand]
    private void CloseWizard()
    {
        IsWizardOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmReactivateAsync()
    {
        // Idempotency: if already secured, treat as success
        if (_rbacOptions.CurrentValue.Enabled)
        {
            StatusMessage = "Already in Secured mode.";
            RequestClose?.Invoke(true);
            return;
        }

        IsSwitching = true;
        StatusMessage = "";

        try
        {
            var (success, error) = await _systemModeClient.SwitchToSecuredReactivateAsync();

            if (success)
            {
                StatusMessage = "Switched to Secured mode. Existing users reactivated.";
                _logger.Info("RBAC", "System switched to Secured mode (reactivation)");
                RequestClose?.Invoke(true);
                _systemModeClient.NotifyLocalModeChange("secured");
            }
            else
            {
                StatusMessage = error ?? "Failed to switch to Secured mode.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            _logger.Error("RBAC", "Failed to switch to Secured mode (reactivation)", ex);
        }
        finally
        {
            IsSwitching = false;
            IsReactivateDialogOpen = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmSwitchToSecuredAsync()
    {
        if (!IsWizardValid) return;

        // Idempotency: if already secured, treat as success
        if (_rbacOptions.CurrentValue.Enabled)
        {
            StatusMessage = "Already in Secured mode.";
            RequestClose?.Invoke(true);
            return;
        }

        IsSwitching = true;
        WizardError = "";

        try
        {
            var (success, error) = await _systemModeClient.SwitchToSecuredAsync(
                WizardUsername, WizardEmail, WizardPassword);

            if (success)
            {
                StatusMessage = "Switched to Secured mode.";
                _logger.Info("RBAC", "System switched to Secured mode");
                IsWizardOpen = false;
                RequestClose?.Invoke(true);
                _systemModeClient.NotifyLocalModeChange("secured");
            }
            else
            {
                WizardError = error ?? "Failed to switch to Secured mode.";
            }
        }
        catch (Exception ex)
        {
            WizardError = $"Error: {ex.Message}";
            _logger.Error("RBAC", "Failed to switch to Secured mode", ex);
        }
        finally
        {
            IsSwitching = false;
        }
    }

    [RelayCommand]
    private void OpenDisableDialog()
    {
        DisableConfirmText = "";
        DisableError = "";
        IsDisableDialogOpen = true;
    }

    [RelayCommand]
    private void CloseDisableDialog()
    {
        IsDisableDialogOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDisable))]
    private async Task ConfirmSwitchToDefaultAsync()
    {
        if (!CanConfirmDisable) return;

        IsSwitching = true;
        DisableError = "";

        try
        {
            var (success, error) = await _systemModeClient.SwitchToDefaultAsync();

            if (success)
            {
                StatusMessage = "Switched to Default mode.";
                _logger.Info("RBAC", "System switched to Default mode");
                IsDisableDialogOpen = false;
                RequestClose?.Invoke(true);
                _systemModeClient.NotifyLocalModeChange("default");
            }
            else
            {
                DisableError = error ?? "Failed to switch to Default mode.";
            }
        }
        catch (Exception ex)
        {
            DisableError = $"Error: {ex.Message}";
            _logger.Error("RBAC", "Failed to switch to Default mode", ex);
        }
        finally
        {
            IsSwitching = false;
        }
    }
}
