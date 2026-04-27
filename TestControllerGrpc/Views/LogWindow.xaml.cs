using System.Windows;
using System.Windows.Controls;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Views;

public partial class LogWindow : Window
{
    private readonly HashSet<string> _selectedAgents = new(StringComparer.OrdinalIgnoreCase);

    public LogWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private ExecutionDashboardVM? VM => DataContext as ExecutionDashboardVM;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateSessionFilter();
        PopulateAgentToggles();
    }

    private void PopulateSessionFilter()
    {
        if (VM == null) return;
        SessionFilter.Items.Clear();
        SessionFilter.Items.Add(new ComboBoxItem { Content = "All Sessions", IsSelected = true });
        foreach (var session in VM.Sessions)
        {
            SessionFilter.Items.Add(new ComboBoxItem
            {
                Content = session.WatchItemTag,
                Tag = session.SessionId
            });
        }
    }

    private void PopulateAgentToggles()
    {
        if (VM == null) return;
        AgentTogglePanel.Items.Clear();

        var agents = VM.Sessions
            .SelectMany(s => s.Agents)
            .Select(a => a.AgentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n);

        foreach (var agent in agents)
        {
            var btn = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = agent,
                FontSize = 10,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(2, 0, 2, 0),
                IsChecked = true,
                Tag = agent,
            };
            btn.Checked += AgentToggle_Changed;
            btn.Unchecked += AgentToggle_Changed;
            _selectedAgents.Add(agent);
            AgentTogglePanel.Items.Add(btn);
        }
    }

    private void AgentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton btn && btn.Tag is string agent)
        {
            if (btn.IsChecked == true)
                _selectedAgents.Add(agent);
            else
                _selectedAgents.Remove(agent);
        }
        ApplyFilters();
    }

    private void SessionFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VM == null) return;
        var item = SessionFilter.SelectedItem as ComboBoxItem;
        VM.SelectedSessionId = item?.Tag as string;
        ApplyFilters();
    }

    private void SeverityFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VM == null) return;
        var item = SeverityFilter.SelectedItem as ComboBoxItem;
        VM.LogSeverityFilter = item?.Content?.ToString() ?? "All";
        ApplyFilters();
    }

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (VM == null) return;
        VM.LogSearchText = LogSearchBox.Text;
        ApplyFilters();
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        VM?.LogEntries.Clear();
    }

    private void ApplyFilters()
    {
        if (VM == null) return;
        var view = System.Windows.Data.CollectionViewSource
            .GetDefaultView(VM.LogEntries);
        view.Filter = obj =>
        {
            if (obj is not LogEntryVM entry) return false;
            if (!VM.FilterLogEntry(entry)) return false;

            // Additional agent toggle filter
            if (!string.IsNullOrEmpty(entry.AgentName) &&
                _selectedAgents.Count > 0 &&
                !_selectedAgents.Contains(entry.AgentName))
                return false;

            return true;
        };
    }
}
