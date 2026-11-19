using System.Windows;
using System.Windows.Controls;

namespace TestControllerGrpc.Views.AgentWorkspace;

public partial class MaintenanceView : UserControl
{
    private const double AllColumnsWidth = 900;
    private const double DropDetectedByWidth = 700;
    private const double ActiveShareOfHeight = 0.45;
    private const double UpdatesMinHeight = 140;

    // Remembers the user's splitter position across a collapse/expand of Windows updates.
    private GridLength _updatesHeight = new(1, GridUnitType.Star);

    public MaintenanceView()
    {
        InitializeComponent();
        SizeChanged += OnPanelSizeChanged;
        Loaded += (_, _) => UpdateSectionRows();
    }

    // Code rather than a trigger: DataGridColumn is not in the visual tree, so it cannot resolve a
    // RelativeSource binding to the panel's ActualWidth without a BindingProxy freezable.
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
            ActiveScroll.MaxHeight = Math.Max(120, e.NewSize.Height * ActiveShareOfHeight);

        if (!e.WidthChanged) return;

        double width = e.NewSize.Width;
        DetectedByColumn.Visibility = width >= AllColumnsWidth ? Visibility.Visible : Visibility.Collapsed;
        LastInstallColumn.Visibility = width >= DropDetectedByWidth ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUpdatesExpanderToggled(object sender, RoutedEventArgs e) => UpdateSectionRows();

    private void OnUpdatesExpanderVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateSectionRows();

    // Windows updates and History share the space below In progress. When updates is hidden or
    // collapsed to its header, its row shrinks to fit and History takes the rest; the splitter goes too.
    private void UpdateSectionRows()
    {
        if (UpdatesExpander is null || UpdatesRow is null || SplitterRow is null || SectionSplitter is null) return;

        bool open = UpdatesExpander.IsVisible && UpdatesExpander.IsExpanded;
        if (open)
        {
            UpdatesRow.Height = _updatesHeight;
            UpdatesRow.MinHeight = UpdatesMinHeight;
            SplitterRow.Height = new GridLength(6);
            SectionSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (UpdatesRow.Height.IsStar) _updatesHeight = UpdatesRow.Height;
            UpdatesRow.Height = GridLength.Auto;
            UpdatesRow.MinHeight = 0;
            SplitterRow.Height = new GridLength(0);
            SectionSplitter.Visibility = Visibility.Collapsed;
        }
    }
}
