using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestControllerGrpc.Views.Results;

/// <summary>Pass-rate (0–100) → pixel height for the trend strip bars.</summary>
public sealed class RateToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rate = value is double d ? d : 0;
        var max = 80.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            max = p;
        return Math.Max(0, Math.Min(max, rate / 100.0 * max));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Pass-rate (0–100) → pixel width for the CI progress bars.</summary>
public sealed class RateToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rate = value is double d ? d : 0;
        var max = 120.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            max = p;
        return Math.Max(0, Math.Min(max, rate / 100.0 * max));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Integer count → star <see cref="GridLength"/> for proportional stacked bars.</summary>
public sealed class IntToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        return new GridLength(Math.Max(0, n), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Joins a string list with a separator (parameter, default ", ").</summary>
public sealed class StringJoinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable items and not string)
        {
            var sep = parameter as string ?? ", ";
            return string.Join(sep, items.Cast<object?>().Where(o => o is not null));
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
