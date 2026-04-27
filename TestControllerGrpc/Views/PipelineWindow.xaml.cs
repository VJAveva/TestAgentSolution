using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Views;

public partial class PipelineWindow : Window
{
    public PipelineWindow()
    {
        InitializeComponent();
    }

    private ExecutionDashboardVM? VM => DataContext as ExecutionDashboardVM;

    private void SessionHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SessionCardVM card)
        {
            card.IsExpanded = !card.IsExpanded;
            VM?.SelectSession(card.SessionId);
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var filter = tb.Text.Trim().ToLowerInvariant();

        foreach (var item in SessionList.Items)
        {
            if (item is SessionCardVM card)
            {
                var container = SessionList.ItemContainerGenerator
                    .ContainerFromItem(card) as FrameworkElement;
                if (container == null) continue;

                container.Visibility = string.IsNullOrEmpty(filter)
                    || card.WatchItemTag.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || card.Agents.Any(a => a.AgentName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
        }
    }
}
