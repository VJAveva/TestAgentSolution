using System.Windows;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class ResultsDashboardWindow : Window
{
    public ResultsDashboardWindow(BuildResultsViewModel vm)
    {
        // Register the converter before XAML parsing so StaticResource can resolve it
        Resources["PassRateConv"] = vm.PassRateConverter;

        InitializeComponent();
        DataContext = vm;
    }
}
