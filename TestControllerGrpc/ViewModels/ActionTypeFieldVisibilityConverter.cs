using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels;

public sealed class ActionTypeFieldVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string current && parameter is string allowed)
        {
            foreach (var p in allowed.Split('|'))
                if (string.Equals(current.Trim(), p.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
