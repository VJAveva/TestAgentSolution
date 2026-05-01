using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Views;

public partial class ExecutionDashboardWindow : Window
{
    private TimelineVM? _timeline;
    private readonly HashSet<string> _selectedAgents = new(StringComparer.OrdinalIgnoreCase);

    // Named element references resolved after load
    private ItemsControl? _pipelineSessionList;
    private Canvas? _timeAxis;
    private ComboBox? _sessionFilter;
    private ComboBox? _severityFilter;
    private ItemsControl? _agentTogglePanel;
    private TextBox? _logSearchBox;

    // Tray + close interception
    private TrayIconService? _tray;

    /// <summary>
    /// When true, the next close request actually closes the window. Set this
    /// before calling <see cref="Window.Close"/> from a tray "Exit" handler.
    /// Default behavior on close is to hide the window to the tray.
    /// </summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when the window hides itself to the system tray.</summary>
    public event EventHandler? TrayClosed;

    public ExecutionDashboardWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private ExecutionDashboardVM? VM => DataContext as ExecutionDashboardVM;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (VM == null) return;

        // Resolve named elements from visual tree
        _pipelineSessionList = FindDescendant<ItemsControl>(this, "PipelineSessionList");
        _timeAxis = FindDescendant<Canvas>(this, "TimeAxis");
        _sessionFilter = FindDescendant<ComboBox>(this, "SessionFilter");
        _severityFilter = FindDescendant<ComboBox>(this, "SeverityFilter");
        _agentTogglePanel = FindDescendant<ItemsControl>(this, "AgentTogglePanel");
        _logSearchBox = FindDescendant<TextBox>(this, "LogSearchBox");

        // Create and bind the TimelineVM
        _timeline = new TimelineVM(VM, Dispatcher);
        _timeline.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TimelineVM.CursorSeconds) or nameof(TimelineVM.TotalSeconds))
                UpdateTimeAxis();
        };
        VM.Timeline = _timeline;

        PopulateSessionFilter();
        PopulateAgentToggles();

        // Install tray icon (hides instead of closing)
        _tray = new TrayIconService(this, "Execution Dashboard - TestControllerGrpc");
        _tray.Install();
        _tray.ExitRequested += (_, _) =>
        {
            AllowClose = true;
            Close();
        };
    }

    /// <summary>
    /// Window close interception: hide-to-tray unless <see cref="AllowClose"/>
    /// has been set (e.g. by the tray "Exit" menu, or app shutdown).
    /// Pressing X / Alt+F4 follows the spec behavior.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose && _tray != null)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloon(
                "Execution Dashboard",
                "Still running in the system tray. Double-click the icon to reopen.");
            TrayClosed?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnClosing(e);
    }

    // ?? Pipeline Tab ????????????????????????????????????????????????

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

    private void PipelineFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || VM == null) return;
        VM.PipelineSearchText = tb.Text;
        ApplyPipelineFilter();
    }

    private void PipelineStatusFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || VM == null) return;
        VM.PipelineStatusFilter = rb.Tag as string ?? "All";
        ApplyPipelineFilter();
    }

    /// <summary>
    /// Applies the Pipeline view's combined status + free-text filter using the
    /// VM's predicate. Uses CollectionView.Filter so virtualization is preserved
    /// and the source ObservableCollection isn't mutated.
    /// </summary>
    private void ApplyPipelineFilter()
    {
        if (VM == null) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(VM.Sessions);
        view.Filter = obj => obj is not SessionCardVM card || VM.FilterSessionCard(card);
    }

    // ?? Timeline Tab ????????????????????????????????????????????????

    private void UpdateTimeAxis()
    {
        if (_timeline == null || _timeAxis == null) return;

        _timeAxis.Children.Clear();
        var width = _timeAxis.ActualWidth > 0 ? _timeAxis.ActualWidth : 1000;
        var totalSec = _timeline.TotalSeconds;
        if (totalSec <= 0) return;

        for (double t = 0; t <= totalSec; t += 15)
        {
            var x = t / totalSec * width;
            var line = new Line
            {
                X1 = x, Y1 = 18, X2 = x, Y2 = 24,
                Stroke = FindResource("TextDim") as Brush,
                StrokeThickness = 0.5
            };
            _timeAxis.Children.Add(line);

            var minutes = (int)(t / 60);
            var seconds = (int)(t % 60);
            var label = new TextBlock
            {
                Text = $"{minutes}:{seconds:D2}",
                FontSize = 9,
                Foreground = FindResource("TextDim") as Brush
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 2);
            _timeAxis.Children.Add(label);
        }
    }

    private void BarCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Canvas canvas || _timeline == null) return;
        var width = canvas.ActualWidth;
        var totalSec = _timeline.TotalSeconds;
        if (totalSec <= 0 || width <= 0) return;

        foreach (var child in canvas.Children)
        {
            if (child is ItemsControl ic)
            {
                for (int i = 0; i < ic.Items.Count; i++)
                {
                    if (ic.Items[i] is TimelineBar bar &&
                        ic.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                    {
                        var left = bar.OffsetSeconds / totalSec * width;
                        var barWidth = Math.Max(4, bar.DurationSeconds / totalSec * width);
                        Canvas.SetLeft(cp, left);
                        cp.Width = barWidth;
                    }
                }
            }

            if (child is Line line && line.Name == "CursorLine")
            {
                var cx = _timeline.CursorSeconds / totalSec * width;
                Canvas.SetLeft(line, cx);
            }
        }
    }

    // ?? Unified Log Tab ?????????????????????????????????????????????

    private void PopulateSessionFilter()
    {
        if (VM == null || _sessionFilter == null) return;
        _sessionFilter.Items.Clear();
        _sessionFilter.Items.Add(new ComboBoxItem { Content = "All Sessions", IsSelected = true });
        foreach (var session in VM.Sessions)
        {
            _sessionFilter.Items.Add(new ComboBoxItem
            {
                Content = session.WatchItemTag,
                Tag = session.SessionId
            });
        }
    }

    private void PopulateAgentToggles()
    {
        if (VM == null || _agentTogglePanel == null) return;
        _agentTogglePanel.Items.Clear();

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
            _agentTogglePanel.Items.Add(btn);
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
        ApplyLogFilters();
    }

    private void SessionFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VM == null || _sessionFilter == null) return;
        var item = _sessionFilter.SelectedItem as ComboBoxItem;
        VM.SelectedSessionId = item?.Tag as string;
        ApplyLogFilters();
    }

    private void SeverityFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VM == null || _severityFilter == null) return;
        var item = _severityFilter.SelectedItem as ComboBoxItem;
        VM.LogSeverityFilter = item?.Content?.ToString() ?? "All";
        ApplyLogFilters();
    }

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (VM == null || _logSearchBox == null) return;
        VM.LogSearchText = _logSearchBox.Text;
        ApplyLogFilters();
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

    private void ApplyLogFilters()
    {
        if (VM == null) return;
        var view = System.Windows.Data.CollectionViewSource
            .GetDefaultView(VM.LogEntries);
        view.Filter = obj =>
        {
            if (obj is not LogEntryVM entry) return false;
            if (!VM.FilterLogEntry(entry)) return false;

            if (!string.IsNullOrEmpty(entry.AgentName) &&
                _selectedAgents.Count > 0 &&
                !_selectedAgents.Contains(entry.AgentName))
                return false;

            return true;
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _timeline?.Dispose();
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    // ?? Helpers ??????????????????????????????????????????????????????

    /// <summary>Finds a named descendant element in the visual tree.</summary>
    private static T? FindDescendant<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name)
                return fe;
            var result = FindDescendant<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
