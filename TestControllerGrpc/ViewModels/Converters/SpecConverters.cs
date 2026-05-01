using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TestControllerGrpc.ViewModels.Converters.Spec;

/// <summary>
/// Internal helper that resolves a brush from <see cref="Application.Current"/>'s
/// merged resource dictionaries by key. Falls back to <see cref="Brushes.Transparent"/>
/// when the key is missing or no Application is running (e.g. design surface).
/// </summary>
internal static class ResourceLookup
{
    public static Brush Brush(string key)
    {
        var app = Application.Current;
        if (app == null) return System.Windows.Media.Brushes.Transparent;
        return app.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Transparent;
    }
}

/// <summary>
/// Normalises an action status value (either the spec's
/// <c>ActionStatus</c> enum or the legacy string used by the existing
/// <c>ActionPillVM</c>) to a single canonical token.
/// </summary>
internal static class StatusNormalizer
{
    /// <summary>
    /// Returns one of: Pending, Running, Done, Failed, Rebooting, Idle.
    /// </summary>
    public static string Action(object? value) => value switch
    {
        null => "Pending",
        Enum e => e.ToString(),
        string s => s switch
        {
            "Success" => "Done",
            "Executing" => "Running",
            _ => string.IsNullOrWhiteSpace(s) ? "Pending" : s,
        },
        _ => "Pending",
    };

    /// <summary>
    /// Returns one of: Idle, Running, Completed, Failed, Cancelled.
    /// </summary>
    public static string Session(object? value) => value switch
    {
        null => "Idle",
        Enum e => e.ToString(),
        string s => s switch
        {
            "Success" => "Completed",
            "Done" => "Completed",
            _ => string.IsNullOrWhiteSpace(s) ? "Idle" : s,
        },
        _ => "Idle",
    };

    /// <summary>
    /// Returns one of: Idle, Executing, Failed, Rebooting, Done, Waiting.
    /// </summary>
    public static string Agent(object? value) => value switch
    {
        null => "Idle",
        Enum e => e.ToString(),
        string s => s switch
        {
            "Success" => "Done",
            "Running" => "Executing",
            _ => string.IsNullOrWhiteSpace(s) ? "Idle" : s,
        },
        _ => "Idle",
    };

    /// <summary>
    /// Returns one of: Info, Warning, Error, Success.
    /// </summary>
    public static string Log(object? value) => value switch
    {
        null => "Info",
        Enum e => e.ToString(),
        string s => string.IsNullOrWhiteSpace(s) ? "Info" : s,
        _ => "Info",
    };
}

/// <summary>Maps an action status to its tinted background brush.</summary>
public sealed class ActionStatusToBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Action(value) switch
        {
            "Done"      => ResourceLookup.Brush("BgSuccessBrush"),
            "Running"   => ResourceLookup.Brush("BgInfoBrush"),
            "Failed"    => ResourceLookup.Brush("BgDangerBrush"),
            "Rebooting" => ResourceLookup.Brush("BgWarningBrush"),
            _           => System.Windows.Media.Brushes.Transparent,
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps an action status to its border brush.</summary>
public sealed class ActionStatusToBorderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Action(value) switch
        {
            "Done"      => ResourceLookup.Brush("BorderSuccessBrush"),
            "Running"   => ResourceLookup.Brush("BorderInfoBrush"),
            "Failed"    => ResourceLookup.Brush("BorderDangerBrush"),
            "Rebooting" => ResourceLookup.Brush("BorderWarningBrush"),
            _           => ResourceLookup.Brush("BorderTertiaryBrush"),
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps an action status to its foreground (glyph + label) brush.</summary>
public sealed class ActionStatusToFgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Action(value) switch
        {
            "Done"      => ResourceLookup.Brush("TextSuccessBrush"),
            "Running"   => ResourceLookup.Brush("TextInfoBrush"),
            "Failed"    => ResourceLookup.Brush("TextDangerBrush"),
            "Rebooting" => ResourceLookup.Brush("TextWarningBrush"),
            _           => ResourceLookup.Brush("TextTertiaryBrush"),
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Maps an action status to the unicode glyph the chip should display:
/// ? (Done), ? (Running), ? (Failed), ? (Rebooting), ? (Pending).
/// </summary>
public sealed class ActionStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Action(value) switch
        {
            "Done"      => "\u2713", // ?
            "Running"   => "\u25CF", // ?
            "Failed"    => "\u2715", // ?
            "Rebooting" => "\u21BB", // ?
            _           => "\u25CB", // ?
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps a session status to its tinted background brush.</summary>
public sealed class SessionStatusToBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Session(value) switch
        {
            "Running"   => ResourceLookup.Brush("BgInfoBrush"),
            "Completed" => ResourceLookup.Brush("BgSuccessBrush"),
            "Failed"    => ResourceLookup.Brush("BgDangerBrush"),
            _           => ResourceLookup.Brush("BgChipBrush"),
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps a session status to its foreground brush.</summary>
public sealed class SessionStatusToFgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Session(value) switch
        {
            "Running"   => ResourceLookup.Brush("TextInfoBrush"),
            "Completed" => ResourceLookup.Brush("TextSuccessBrush"),
            "Failed"    => ResourceLookup.Brush("TextDangerBrush"),
            _           => ResourceLookup.Brush("TextSecondaryBrush"),
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps a session status to a human label for the badge text.</summary>
public sealed class SessionStatusToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Session(value) switch
        {
            "Running"   => "Running",
            "Completed" => "Completed",
            "Failed"    => "Failed",
            "Cancelled" => "Cancelled",
            _           => "Idle",
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps an agent state to its foreground brush.</summary>
public sealed class AgentStateToFgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Agent(value) switch
        {
            "Executing" => ResourceLookup.Brush("TextInfoBrush"),
            "Idle"      => ResourceLookup.Brush("TextSuccessBrush"),
            "Failed"    => ResourceLookup.Brush("TextDangerBrush"),
            "Rebooting" => ResourceLookup.Brush("TextWarningBrush"),
            "Done"      => ResourceLookup.Brush("TextSuccessBrush"),
            "Waiting"   => ResourceLookup.Brush("TextSecondaryBrush"),
            _           => ResourceLookup.Brush("TextSecondaryBrush"),
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Same colour mapping as <see cref="AgentStateToFgConverter"/>; used to
/// fill the 3px progress bar under the agent name.
/// </summary>
public sealed class AgentStateToProgressBrushConverter : IValueConverter
{
    private static readonly AgentStateToFgConverter _inner = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => _inner.Convert(value, targetType, parameter, culture);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Maps a log level (enum or string: Error/Warning/Success/Info) to a brush.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusNormalizer.Log(value) switch
        {
            "Error"   => ResourceLookup.Brush("TextDangerBrush"),
            "Warning" => ResourceLookup.Brush("TextWarningBrush"),
            "Success" => ResourceLookup.Brush("TextSuccessBrush"),
            _         => ResourceLookup.Brush("TextSecondaryBrush"),
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True ? Visible, false ? Collapsed.</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Non-empty string ? Visible, otherwise Collapsed.</summary>
public sealed class NotEmptyToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Compares <c>value</c> to <c>parameter</c> for equality; returns true when equal.
/// Used by filter RadioButtons in OneWay mode. ConvertBack returns the parameter
/// when the radio is checked (so two-way bindings can push the value back), and
/// <see cref="Binding.DoNothing"/> otherwise to avoid feedback loops.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.Equals(parameter)
            || string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? (parameter ?? Binding.DoNothing) : Binding.DoNothing;
}

/// <summary>
/// Multiplies a percentage (0-100) by a pixel-width parameter and returns
/// the resulting pixel width. Used by the Gantt and the agent progress bar
/// when a fixed container width is known. Pass the container width as the
/// converter parameter (e.g. <c>ConverterParameter=104</c>).
/// </summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            float f  => (double)f,
            int i    => (double)i,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0.0,
        };

        var max = 100.0;
        if (parameter is double pd) max = pd;
        else if (parameter is int pi) max = pi;
        else if (parameter is string ps && !string.IsNullOrWhiteSpace(ps) && ps != "*"
                 && double.TryParse(ps, NumberStyles.Any, CultureInfo.InvariantCulture, out var pdv))
            max = pdv;

        return Math.Max(0, Math.Min(max, percent / 100.0 * max));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
