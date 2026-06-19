using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Settings;

namespace TestControllerGrpc.Views.Settings;

public partial class SecurityModePanel : UserControl
{
    private readonly SecurityModeViewModel _vm;

    public SecurityModePanel()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<SecurityModeViewModel>();
        DataContext = _vm;

        // The view model is a DI singleton. Scope the subscription to this panel's loaded
        // lifetime so stale instances (whose host window has been closed) don't keep handling
        // events — that caused "Cannot set Owner property to a Window that has been closed".
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    /// <summary>
    /// Assigns the dialog owner only when the host window is still open. A closed (or null)
    /// owner throws on <see cref="Window.Owner"/>; in that case the dialog opens un-owned.
    /// </summary>
    private void SetOwnerSafe(Window dialog)
    {
        var owner = Window.GetWindow(this);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecurityModeViewModel.IsWizardOpen) && _vm.IsWizardOpen)
        {
            ShowWizard();
        }
        else if (e.PropertyName == nameof(SecurityModeViewModel.IsDisableDialogOpen) && _vm.IsDisableDialogOpen)
        {
            ShowDisableDialog();
        }
        else if (e.PropertyName == nameof(SecurityModeViewModel.IsReactivateDialogOpen) && _vm.IsReactivateDialogOpen)
        {
            ShowReactivateConfirmation();
        }
    }

    private void ShowWizard()
    {
        try
        {
            var wizard = new InitialAdminWizard();
            SetOwnerSafe(wizard);
            wizard.ShowDialog();
            // Navigation (LoginPage) is handled by MainWindow's ModeChanged handler.
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<IAppLogger>();
            logger.Error("RBAC", "Failed to open Initial Admin Wizard", ex);
            _vm.WizardError = $"Failed to open wizard: {ex.Message}";
        }
        finally
        {
            _vm.IsWizardOpen = false;
        }
    }

    private void ShowDisableDialog()
    {
        try
        {
            var dialog = new DisableRbacConfirmDialog();
            SetOwnerSafe(dialog);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<IAppLogger>();
            logger.Error("RBAC", "Failed to open Disable RBAC dialog", ex);
            _vm.DisableError = $"Failed to open dialog: {ex.Message}";
        }
        finally
        {
            _vm.IsDisableDialogOpen = false;
        }
    }

    private async void ShowReactivateConfirmation()
    {
        try
        {
            var owner = Window.GetWindow(this);
            var result = owner is { IsLoaded: true }
                ? MessageBox.Show(
                    owner,
                    "Switch to Secured mode?\n\nExisting users will be reactivated and login will be required.",
                    "Confirm Switch to Secured Mode",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question)
                : MessageBox.Show(
                    "Switch to Secured mode?\n\nExisting users will be reactivated and login will be required.",
                    "Confirm Switch to Secured Mode",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

            if (result == MessageBoxResult.OK)
            {
                await _vm.ConfirmReactivateCommand.ExecuteAsync(null);
                // Navigation (LoginPage) is handled by MainWindow's ModeChanged handler.
            }
            else
            {
                _vm.IsReactivateDialogOpen = false;
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<IAppLogger>();
            logger.Error("RBAC", "Failed to show reactivation confirmation", ex);
            _vm.StatusMessage = $"Error: {ex.Message}";
            _vm.IsReactivateDialogOpen = false;
        }
    }
}
