using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Settings;

namespace TestControllerGrpc.Views.Settings;

public partial class DisableRbacConfirmDialog : Window
{
    private readonly SecurityModeViewModel _vm;

    public DisableRbacConfirmDialog()
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
}
