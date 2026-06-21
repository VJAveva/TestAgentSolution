using System.Windows.Controls;
using TestAgent.Diagnostics.ViewModels;

namespace TestAgent.Diagnostics.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _vm;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.RecordsAppended -= ScrollToTail;
        _vm = e.NewValue as LogsViewModel;
        if (_vm is not null) _vm.RecordsAppended += ScrollToTail;
    }

    private void ScrollToTail()
    {
        if (LogList.Items.Count == 0) return;
        var last = LogList.Items[^1];
        LogList.ScrollIntoView(last);
    }
}
