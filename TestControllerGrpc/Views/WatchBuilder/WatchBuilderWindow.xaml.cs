using System.Windows;
using System.Windows.Controls;
using TestControllerGrpc.ViewModels.WatchBuilder;

namespace TestControllerGrpc.Views.WatchBuilder;

public partial class WatchBuilderWindow : Window
{
    private readonly WatchBuilderViewModel _vm;

    public WatchBuilderWindow(WatchBuilderViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectedNode = e.NewValue as WatchBuilderNodeViewModel;
    }
}
