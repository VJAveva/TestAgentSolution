using System.Windows;
using System.Windows.Controls;

namespace TestControllerGrpc.Views.AgentWorkspace;

public partial class MaintenanceView : UserControl
{
    private const double AllColumnsWidth = 900;
    private const double DropDetectedByWidth = 700;

    public MaintenanceView()
    {
        InitializeComponent();
        SizeChanged += OnPanelSizeChanged;
    }

    // Code rather than a trigger: DataGridColumn is not in the visual tree, so it cannot resolve a
    // RelativeSource binding to the panel's ActualWidth without a BindingProxy freezable.
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        double width = e.NewSize.Width;
        DetectedByColumn.Visibility = width >= AllColumnsWidth ? Visibility.Visible : Visibility.Collapsed;
        LastInstallColumn.Visibility = width >= DropDetectedByWidth ? Visibility.Visible : Visibility.Collapsed;
    }
}
