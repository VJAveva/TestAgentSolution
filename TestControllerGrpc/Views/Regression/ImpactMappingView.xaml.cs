using System.Windows.Controls;
using TestControllerGrpc.ViewModels.Regression;

namespace TestControllerGrpc.Views.Regression;

/// <summary>The impact-mapping panel for the Regression tab (P30). Binds to <see cref="ImpactMappingViewModel"/>.</summary>
public partial class ImpactMappingView : UserControl
{
    /// <summary>Creates the view and binds it to the DI-resolved view model.</summary>
    public ImpactMappingView(ImpactMappingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
