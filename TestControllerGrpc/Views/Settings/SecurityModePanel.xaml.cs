using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Settings;
using TestControllerGrpc.Views.Login;

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
    }

    private void ShowWizard()
    {
        try
        {
            var wizard = new InitialAdminWizard();
            wizard.Owner = Window.GetWindow(this);
            var result = wizard.ShowDialog();

            // After successful switch to Secured mode, route to LoginPage
            if (result == true && _vm.IsSecuredMode)
            {
                var ownerWindow = Window.GetWindow(this);
                var loginPage = new LoginPage();
                loginPage.Show();
                ownerWindow?.Close();
            }
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
}
