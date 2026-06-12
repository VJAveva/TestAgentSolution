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

    // Initial Admin Wizard fields
    [ObservableProperty] private bool _isWizardOpen;
    [ObservableProperty] private string _wizardUsername = "";
    [ObservableProperty] private string _wizardEmail = "";
    [ObservableProperty] private string _wizardPassword = "";
    [ObservableProperty] private string _wizardConfirmPassword = "";
    [ObservableProperty] private string _wizardError = "";

    // Disable RBAC confirmation fields
    [ObservableProperty] private bool _isDisableDialogOpen;
    [ObservableProperty] private string _disableConfirmText = "";
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

    public bool CanConfirmDisable => string.Equals(DisableConfirmText, DisableConfirmString, StringComparison.Ordinal);

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
    private void OpenWizard()
    {
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
    private async Task ConfirmSwitchToSecuredAsync()
    {
        if (!IsWizardValid) return;

        IsSwitching = true;
        WizardError = "";

        try
        {
            var (success, error) = await _systemModeClient.SwitchToSecuredAsync(
                WizardUsername, WizardEmail, WizardPassword);

            if (success)
            {
                IsWizardOpen = false;
                StatusMessage = "Switched to Secured mode.";
                _logger.Info("RBAC", "System switched to Secured mode");
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

    [RelayCommand]
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
                IsDisableDialogOpen = false;
                StatusMessage = "Switched to Default mode.";
                _logger.Info("RBAC", "System switched to Default mode");
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
