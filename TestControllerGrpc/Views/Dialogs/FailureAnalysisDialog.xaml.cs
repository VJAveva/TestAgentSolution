using System.Windows;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

public partial class FailureAnalysisDialog : Window
{
    private readonly FailureAnalysisVM _vm;

    public FailureAnalysisDialog(FailureAnalysisVM vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.ToClipboardText());
        }
        catch
        {
            MessageBox.Show("Could not copy to clipboard.", "Copy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnViewLogClick(object sender, RoutedEventArgs e)
    {
        var build = _vm.LatestBuildName;
        if (string.IsNullOrEmpty(build))
        {
            MessageBox.Show("No build available to view log for.", "View Log",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = await ExecutionLogViewerDialog.CreateAsync(
            build, _vm.TestCaseName, _vm.LatestFailedStepIndex, this);
        dialog?.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
