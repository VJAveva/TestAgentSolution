using System.Windows;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class FeatureSettingsWindow : Window
{
    public FeatureSettingsWindow(FeatureSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
