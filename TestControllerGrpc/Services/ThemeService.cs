using System.Windows;
using TestControllerGrpc.Helpers;

namespace TestControllerGrpc.Services;

/// <summary>
/// Manages runtime theme switching by replacing the first MergedDictionary
/// in Application.Resources with the selected theme ResourceDictionary.
/// </summary>
public static class ThemeService
{
    public static readonly string[] AvailableThemes = ["Dark", "Light", "High Contrast"];

    /// <summary>The theme last applied. Matches the default merged in App.xaml until ApplyTheme runs.</summary>
    public static string CurrentTheme { get; private set; } = "Dark";

    /// <summary>
    /// Raised after a theme has been applied, with the new theme name.
    /// </summary>
    /// <remarks>
    /// For anything a ResourceDictionary swap cannot reach - notably AvalonEdit, whose syntax
    /// highlighting is a frozen object graph rather than a DynamicResource lookup. Subscribers are
    /// static-rooted, so a Window MUST unsubscribe when it closes or it is leaked for the process
    /// lifetime.
    /// </remarks>
    public static event Action<string>? ThemeChanged;

    /// <summary>
    /// Applies the named theme by loading its ResourceDictionary and replacing
    /// the current theme dictionary in Application.Resources.
    /// </summary>
    public static void ApplyTheme(string themeName)
    {
        var app = Application.Current;
        if (app is null) return;

        var uri = themeName switch
        {
            "Light" => new Uri("Themes/LightTheme.xaml", UriKind.Relative),
            "High Contrast" => new Uri("Themes/HighContrastTheme.xaml", UriKind.Relative),
            _ => new Uri("Themes/DarkTheme.xaml", UriKind.Relative),
        };

        var newDict = new ResourceDictionary { Source = uri };
        var merged = app.Resources.MergedDictionaries;

        if (merged.Count > 0)
            merged[0] = newDict;
        else
            merged.Insert(0, newDict);

        // Flush rasterized SVG icon cache so icons re-render with new theme colors
        SvgIconHelper.ClearCache();

        CurrentTheme = themeName;
        ThemeChanged?.Invoke(themeName);
    }
}
