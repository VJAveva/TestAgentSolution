using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Regression;

namespace TestControllerGrpc.Views.Regression;

public partial class RegressionWindow : Window
{
    public RegressionWindow(RegressionViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Bound straight to the singleton job so the strip keeps reporting after the sign-in dialog closes.
        IndexRebuildStrip.DataContext = App.Services.GetRequiredService<IImpactIndexRebuildService>();
    }

    private void OnCancelRebuildClick(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<IImpactIndexRebuildService>().Cancel();

    // Opens the AVEVA / Microsoft interactive sign-in dialog, then reloads live data if signed in.
    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        var vm = App.Services.GetRequiredService<AdoSignInViewModel>();
        var dlg = new AdoSignInDialog(vm) { Owner = this };
        dlg.ShowDialog();
        if (vm.IsSignedIn && DataContext is RegressionViewModel rvm)
            rvm.OnSignedInReload();
    }

    // Expand the newly-selected row's details and collapse the previously-selected one (one open at a time).
    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems)
            if (item is SubsystemRowViewModel removed)
                removed.IsExpanded = false;
        foreach (var item in e.AddedItems)
            if (item is SubsystemRowViewModel added)
                added.IsExpanded = true;
    }

    // "Hide details" button inside the row detail pane.
    private void OnHideDetails(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SubsystemRowViewModel vm)
        {
            vm.IsExpanded = false;
            // Selection is what expands a row, so a collapsed row must not stay selected: SelectionChanged
            // would never fire for it again and clicking it could not reopen it.
            if (ReferenceEquals(ChurnGrid.SelectedItem, vm))
                ChurnGrid.SelectedItem = null;
        }
    }

    // Show/hide the Subsystem (Solutions) column; off by default because its wrapped .sln lists make rows uneven.
    private void OnToggleSubsystemsColumn(object sender, RoutedEventArgs e)
    {
        if (SubsystemsColumn is not null)
            SubsystemsColumn.Visibility = SubsystemsColumnToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    // Opens ADO work-item/PR/commit links in the default browser.
    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        var url = e.Uri?.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore launch failures (no default browser / blocked) — non-critical.
        }
        e.Handled = true;
    }
}

/// <summary>Binds a nullable URL string to a Hyperlink's Uri NavigateUri; empty/invalid yields null (no link).</summary>
public sealed class StringToUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when its bound string is null/empty (used by the AI Summary banner).</summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
