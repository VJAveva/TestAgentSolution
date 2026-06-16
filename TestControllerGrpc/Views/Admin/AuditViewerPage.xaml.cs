using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class AuditViewerPage : UserControl
{
    public AuditViewerPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AuditViewerViewModel>();
    }
}
