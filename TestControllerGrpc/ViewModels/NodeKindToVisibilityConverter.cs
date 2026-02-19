using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts a NodeKind string to Visibility.
/// Usage: Visibility="{Binding NodeKind, Converter={StaticResource VisWhen}, ConverterParameter=Action}"
/// Shows the panel only when NodeKind matches the parameter.
/// </summary>
public sealed class NodeKindToVisibilityConverter : IValueConverter
{
    public static readonly NodeKindToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string kind && parameter is string expected)
            return string.Equals(kind, expected, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
