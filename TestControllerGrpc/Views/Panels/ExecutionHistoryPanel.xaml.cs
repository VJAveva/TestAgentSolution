using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Panels;

public partial class ExecutionHistoryPanel : UserControl
{
    public ExecutionHistoryPanel()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ExecutionHistoryPanelVM>();
    }
}
