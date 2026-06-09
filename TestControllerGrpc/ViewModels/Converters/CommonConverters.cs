using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts an ActionType string to Visibility for conditional field display.
/// Usage: ConverterParameter="RunCommand|RunRemoteCommand" � shows when current type matches any.
/// </summary>
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

/// <summary>
/// Converts an ActiveEditingContext string to Visibility.
/// Usage: ConverterParameter=WatchList � shows only when context matches.
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

/// <summary>
/// Inverse of BooleanToVisibilityConverter: true ? Collapsed, false ? Visible.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// Converts a NodeKind string to an icon background brush via DynamicResource lookup.
/// </summary>
public sealed class NodeKindToIconBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as string ?? "";
        var resourceKey = kind switch
        {
            NodeKinds.WatchList or NodeKinds.TemplateList => "IconBgFolder",
            NodeKinds.WatchItem => "IconBgWatch",
            NodeKinds.Event => "IconBgEvent",
            NodeKinds.Template => "IconBgTemplate",
            NodeKinds.ActionGroup => "IconBgGroup",
            NodeKinds.Action => "IconBgAction",
            NodeKinds.Initialize => "IconBgInit",
            NodeKinds.Ref => "IconBgRef",
            _ => "IconBgAction"
        };

        return Application.Current?.TryFindResource(resourceKey) as Brush
            ?? new SolidColorBrush(Color.FromArgb(0x33, 0x89, 0xB4, 0xFA));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a NodeKind string to Visibility.
/// Usage: ConverterParameter=Action � shows panel only when NodeKind matches.
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

/// <summary>
/// Converts null/empty string to Visible (show placeholder), non-empty to Collapsed.
/// </summary>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts a non-empty string to Visible, empty/null to Collapsed.
/// Inverse of <see cref="StringEmptyToVisibilityConverter"/>.
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts an integer greater than zero to Visible, zero or less to Collapsed.
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

// ??? Execution Dashboard converters ????????????????????????????????????

/// <summary>
/// Converts a percentage (0-100) to a pixel width.
/// ConverterParameter optionally specifies the max pixel width (default 100).
/// Accepts int or double inputs.
/// </summary>
public sealed class DashboardPercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            int i    => (double)i,
            double d => d,
            _        => 0.0,
        };
        var maxWidth = 100.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var custom))
            maxWidth = custom;
        return Math.Max(0, Math.Min(maxWidth, percent / 100.0 * maxWidth));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// Returns Visible when the bound string equals "Running", Collapsed otherwise.
/// Used to show the Cancel button only on running session cards.
/// </summary>
public sealed class RunningToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, "Running", StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Returns Visible when the bound value is non-null (and non-empty for strings),
/// Collapsed otherwise.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts an integer value: 0 → Visible, non-zero → Collapsed.
/// Useful for showing "empty state" messages when a collection count is zero.
/// </summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts a latency value (int ms) to a theme-aware brush.
/// &lt;= 100ms → LatencyGood (green), &lt;= 500ms → LatencyWarn (yellow), &gt; 500ms → LatencyBad (red).
/// -1 (not measured) → transparent.
/// </summary>
public sealed class LatencyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int ms || ms < 0)
            return Brushes.Transparent;

        var key = ms switch
        {
            <= 100 => "LatencyGood",
            <= 500 => "LatencyWarn",
            _ => "LatencyBad"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Converts an ExecutionStatus string to a status dot brush. Provides a unified 5-state system:
/// Running → StatusBlue, Success → StatusGreen, Failed → StatusRed,
/// PartialFailure → StatusYellow, Cancelled/Idle → StatusGray.
/// </summary>
public sealed class StatusToDotBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string ?? "";
        var key = status switch
        {
            "Running" => "StatusBlue",
            "Success" => "StatusGreen",
            "Failed" => "StatusRed",
            "PartialFailure" => "StatusYellow",
            "Cancelled" => "StatusGray",
            _ => "StatusGray"
        };

        return Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x58, 0x5B, 0x70));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
