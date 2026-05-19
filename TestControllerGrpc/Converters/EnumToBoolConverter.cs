using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.Converters;

/// <summary>
/// Converts an enum value to bool (true if value matches parameter).
/// Used for RadioButton groups bound to enum properties.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string s)
            return Enum.Parse(targetType, s);
        return DependencyProperty.UnsetValue;
    }
}
