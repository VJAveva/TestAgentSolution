using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TestControllerGrpc.ViewModels.AgentWorkspace;

namespace TestControllerGrpc.Views.AgentWorkspace;

public partial class FleetView : UserControl
{
    public FleetView()
    {
        InitializeComponent();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FleetCardVM card &&
            DataContext is FleetVM vm)
        {
            vm.SelectAgentCommand.Execute(card);
        }
    }
}
