using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts a percentage (0-100) to a pixel width for simple progress bars.
/// ConverterParameter optionally specifies the max pixel width (default 400).
/// </summary>
public sealed class ProgressPercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            double maxWidth = 400;
            if (parameter is string s && double.TryParse(s, out double custom))
                maxWidth = custom;

            return Math.Max(0, Math.Min(maxWidth, percent / 100.0 * maxWidth));
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
