using System.Windows;
using TestControllerGrpc.ViewModels.Results;

namespace TestControllerGrpc.Views.Results;

public partial class BuildReportCardWindow : Window
{
    public BuildReportCardWindow(BuildReportCardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (vm.RefreshCommand.CanExecute(null))
                await vm.RefreshCommand.ExecuteAsync(null);
        };
    }
}
