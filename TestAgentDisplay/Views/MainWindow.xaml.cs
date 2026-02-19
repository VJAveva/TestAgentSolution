using System.Collections.Specialized;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestAgentDisplay.ViewModels;

namespace TestAgentDisplay.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = _vm;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedAgent))
                WireAutoScroll();
        };
    }

    private INotifyCollectionChanged? _currentCollection;

    private void WireAutoScroll()
    {
        if (_currentCollection is not null)
            _currentCollection.CollectionChanged -= OnOutputChanged;

        if (_vm.SelectedAgent is null) return;

        _currentCollection = _vm.SelectedAgent.OutputLines;
        _currentCollection.CollectionChanged += OnOutputChanged;
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        Dispatcher.InvokeAsync(() =>
        {
            if (OutputListBox.Items.Count > 0)
                OutputListBox.ScrollIntoView(OutputListBox.Items[OutputListBox.Items.Count - 1]);
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
