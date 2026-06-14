using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Settings;

namespace TestControllerGrpc.Views.Settings;

public partial class InitialAdminWizard : Window
{
    private readonly SecurityModeViewModel _vm;

    public InitialAdminWizard()
    {
        _vm = App.Services.GetRequiredService<SecurityModeViewModel>();
        DataContext = _vm;
        InitializeComponent();

        _vm.RequestClose += OnRequestClose;
        Closed += (_, _) => _vm.RequestClose -= OnRequestClose;
    }

    private void OnRequestClose(bool success)
    {
        Dispatcher.InvokeAsync(() =>
        {
            DialogResult = success;
        });
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.WizardPassword = PasswordBox.Password;
    }

    private void ConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.WizardConfirmPassword = ConfirmPasswordBox.Password;
    }
}
