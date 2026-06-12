using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Settings;

namespace TestControllerGrpc.Views.Settings;

public partial class DisableRbacConfirmDialog : Window
{
    public DisableRbacConfirmDialog()
    {
        DataContext = App.Services.GetRequiredService<SecurityModeViewModel>();
        InitializeComponent();
    }
}
