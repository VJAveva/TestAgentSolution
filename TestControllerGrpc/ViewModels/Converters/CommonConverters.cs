using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts an ActionType string to Visibility for conditional field display.
/// Usage: ConverterParameter="RunCommand|RunRemoteCommand" — shows when current type matches any.
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
/// Usage: ConverterParameter=WatchList — shows only when context matches.
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
/// Usage: ConverterParameter=Action — shows panel only when NodeKind matches.
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
