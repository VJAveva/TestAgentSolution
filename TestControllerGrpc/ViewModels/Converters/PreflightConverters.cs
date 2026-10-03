using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TestControllerGrpc.Core.Preflight;

namespace TestControllerGrpc.ViewModels.Converters;

/// <summary>Maps a pre-flight status to a theme brush key, so no colour is hard-coded.</summary>
public sealed class PreflightStatusBrushConverter : IValueConverter
{
    /// <summary>"Fg" for the text/pill colour, "Bg" for the pill fill.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as PreflightStatus? ?? PreflightStatus.Pass;
        var wantBackground = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);

        var key = (status, wantBackground) switch
        {
            (PreflightStatus.Fail, false) => "AccRed",
            (PreflightStatus.Fail, true) => "FailedBadgeBg",
            (PreflightStatus.Warn, false) => "AccYellow",
            (PreflightStatus.Warn, true) => "YellowBtnBg",
            (_, false) => "AccGreen",
            (_, true) => "SuccessBadgeBg",
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when the bound string is null or whitespace. Pass "Invert" to collapse
/// when the string HAS content - that drives the search-box placeholder.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter - drives the filter chips.</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    /// <summary>Only a checked chip writes back; unchecking is done by checking another.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>Maps a parameter source layer to its chip brush.</summary>
public sealed class SourceLayerBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var wantBackground = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);
        var key = value as string switch
        {
            "Profile" => wantBackground ? "MauveBtnBg" : "AccMauve",
            "Pipeline" => wantBackground ? "GreenBtnBg" : "AccGreen",
            "Trigger" => wantBackground ? "YellowBtnBg" : "AccYellow",
            _ => wantBackground ? "AccentBtnBg" : "AccBlue",
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
