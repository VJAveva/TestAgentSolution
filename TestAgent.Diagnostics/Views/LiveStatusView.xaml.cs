using System.Windows.Controls;
using TestAgent.Diagnostics.ViewModels;

namespace TestAgent.Diagnostics.Views;

public partial class LiveStatusView : UserControl
{
    public LiveStatusView()
    {
        InitializeComponent();
    }

    private void OnRunDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: RunStatusVM run }
            && DataContext is LiveStatusViewModel vm
            && vm.ViewLogsCommand.CanExecute(run))
        {
            vm.ViewLogsCommand.Execute(run);
        }
    }
}
