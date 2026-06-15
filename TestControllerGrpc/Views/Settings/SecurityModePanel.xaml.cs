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
        _vm.PropertyChanged += OnVmPropertyChanged;
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
            wizard.Owner = Window.GetWindow(this);
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
            dialog.Owner = Window.GetWindow(this);
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
            var result = MessageBox.Show(
                Window.GetWindow(this),
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
