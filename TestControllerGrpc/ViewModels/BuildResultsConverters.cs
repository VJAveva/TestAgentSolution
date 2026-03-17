using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

/// <summary>Converts a pass rate percentage + container width to a pixel width.</summary>
public class PercentToWidthConverter : IMultiValueConverter
{
    public static readonly PercentToWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2
            && values[0] is double percent
            && values[1] is double containerWidth
            && containerWidth > 0)
        {
            return Math.Max(0, Math.Min(containerWidth, containerWidth * percent / 100.0));
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts a hex color string like "#10B981" to a Color.</summary>
public class StringToColorConverter : IValueConverter
{
    public static readonly StringToColorConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch (FormatException) { /* invalid hex color string — fall through to default Gray */ }
        }
        return Colors.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns true when an integer value is greater than zero.</summary>
public class GreaterThanZeroConverter : IValueConverter
{
    public static readonly GreaterThanZeroConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i > 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns Visible when an integer value is greater than zero, Collapsed otherwise.</summary>
public class GreaterThanZeroToVisConverter : IValueConverter
{
    public static readonly GreaterThanZeroToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverse bool-to-visibility: true ? Collapsed, false ? Visible.</summary>
public class ResultsInverseBoolToVisConverter : IValueConverter
{
    public static readonly ResultsInverseBoolToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns Visible when a string is not null/empty, Collapsed otherwise.</summary>
public class StringNotEmptyToVisConverter : IValueConverter
{
    public static readonly StringNotEmptyToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Converts a pass rate percentage to a SolidColorBrush based on configurable thresholds
/// loaded from the BuildResultsConfig (JSON appsettings.json).
/// Green &gt; GoodThreshold, Orange between WarningThreshold and GoodThreshold, Red &lt; WarningThreshold.
/// Default: Green &gt; 95%, Orange 85-95%, Red &lt; 85%.
/// </summary>
public class PassRateToColorConverter : IValueConverter
{
    private double _goodThreshold = 95.0;
    private double _warningThreshold = 85.0;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));

    static PassRateToColorConverter()
    {
        GreenBrush.Freeze();
        OrangeBrush.Freeze();
        RedBrush.Freeze();
    }

    public PassRateToColorConverter() { }

    public PassRateToColorConverter(BuildResultsConfig config)
    {
        _goodThreshold = config.GoodThreshold;
        _warningThreshold = config.WarningThreshold;
    }

    public void UpdateThresholds(double good, double warning)
    {
        _goodThreshold = good;
        _warningThreshold = warning;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            if (percent > _goodThreshold) return GreenBrush;
            if (percent >= _warningThreshold) return OrangeBrush;
            return RedBrush;
        }
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Returns true when the bound enum value equals the converter parameter string.</summary>
public class EnumEqualsToBoolConverter : IValueConverter
{
    public static readonly EnumEqualsToBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
