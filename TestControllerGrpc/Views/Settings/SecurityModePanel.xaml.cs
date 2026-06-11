using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Settings;

namespace TestControllerGrpc.Views.Settings;

public partial class SecurityModePanel : UserControl
{
    public SecurityModePanel()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SecurityModeViewModel>();
    }
}
