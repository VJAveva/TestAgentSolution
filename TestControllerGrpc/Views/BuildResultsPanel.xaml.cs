using System.Windows;
using System.Windows.Controls;

namespace TestControllerGrpc.Views;

public partial class BuildResultsPanel : UserControl
{
    public BuildResultsPanel()
    {
        InitializeComponent();
    }

    private void OnCopyTestName(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string name && !string.IsNullOrEmpty(name))
            Clipboard.SetText(name);
    }
}
