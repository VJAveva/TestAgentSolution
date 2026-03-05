using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts an ActiveEditingContext string to Visibility.
/// Usage: Visibility="{Binding ActiveEditingContext, Converter={StaticResource CtxVis}, ConverterParameter=WatchList}"
/// Shows the element only when the context matches the parameter.
/// </summary>
public sealed class EditingContextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string context && parameter is string expected)
            return string.Equals(context, expected, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
