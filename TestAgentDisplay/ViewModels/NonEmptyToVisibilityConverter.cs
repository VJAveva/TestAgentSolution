using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestAgentDisplay.ViewModels;

/// <summary>
/// Converts a non-empty string to Visible, empty/null to Collapsed.
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
