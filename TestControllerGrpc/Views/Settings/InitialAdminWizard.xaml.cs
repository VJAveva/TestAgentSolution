using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Settings;

namespace TestControllerGrpc.Views.Settings;

public partial class InitialAdminWizard : Window
{
    public InitialAdminWizard()
    {
        DataContext = App.Services.GetRequiredService<SecurityModeViewModel>();
        InitializeComponent();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SecurityModeViewModel vm)
            vm.WizardPassword = PasswordBox.Password;
    }

    private void ConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SecurityModeViewModel vm)
            vm.WizardConfirmPassword = ConfirmPasswordBox.Password;
    }
}
