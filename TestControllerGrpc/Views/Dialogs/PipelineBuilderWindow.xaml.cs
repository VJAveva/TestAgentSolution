using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.PipelineBuilder;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Four-step New Pipeline panel. VM resolved from App.Services per the popup-window convention.</summary>
public partial class PipelineBuilderWindow : Window
{
    public PipelineBuilderWindow()
    {
        DataContext = App.Services.GetRequiredService<PipelineBuilderViewModel>();
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
