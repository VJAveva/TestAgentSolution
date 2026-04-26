using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Views;

public partial class ExecutionDashboardView : UserControl
{
    public ExecutionDashboardView()
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

    private void AgentName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AgentRowVM agent)
            VM?.SelectAgent(agent.AgentName);
        e.Handled = true;
    }

    private void LogSessionBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LogEntryVM entry &&
            !string.IsNullOrEmpty(entry.SessionId))
            VM?.SelectSession(entry.SessionId);
        e.Handled = true;
    }

    private void LogAgentName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LogEntryVM entry &&
            !string.IsNullOrEmpty(entry.AgentName))
            VM?.SelectAgent(entry.AgentName);
        e.Handled = true;
    }
}
