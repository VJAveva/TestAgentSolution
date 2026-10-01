using System.Windows;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Collects an optional skip reason. The reason is shown on the tree row, not just stored.</summary>
public partial class SkipReasonDialog : Window
{
    public SkipReasonDialog(string nodeLabel, string? existingReason = null)
    {
        InitializeComponent();
        NodeLabel.Text = $"Skip {nodeLabel}";
        ReasonBox.Text = existingReason ?? "";
        Loaded += (_, _) => { ReasonBox.Focus(); ReasonBox.CaretIndex = ReasonBox.Text.Length; };
    }

    public string Reason => ReasonBox.Text.Trim();

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
