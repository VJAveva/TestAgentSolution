using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts a string value to Visibility:
/// null/empty/whitespace ? Visible (show placeholder), non-empty ? Collapsed.
/// </summary>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
