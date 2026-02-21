using System.Windows;

namespace TestControllerGrpc.Services;

/// <summary>
/// Manages runtime theme switching by replacing the first MergedDictionary
/// in Application.Resources with the selected theme ResourceDictionary.
/// </summary>
public static class ThemeService
{
    public static readonly string[] AvailableThemes = ["Dark", "Light", "High Contrast"];

    /// <summary>
    /// Applies the named theme by loading its ResourceDictionary and replacing
    /// the current theme dictionary in Application.Resources.
    /// </summary>
    public static void ApplyTheme(string themeName)
    {
        var uri = themeName switch
        {
            "Light" => new Uri("Themes/LightTheme.xaml", UriKind.Relative),
            "High Contrast" => new Uri("Themes/HighContrastTheme.xaml", UriKind.Relative),
            _ => new Uri("Themes/DarkTheme.xaml", UriKind.Relative),
        };

        var newDict = new ResourceDictionary { Source = uri };
        var appResources = Application.Current.Resources;

        // Replace the first merged dictionary (the theme dictionary)
        if (appResources.MergedDictionaries.Count > 0)
            appResources.MergedDictionaries[0] = newDict;
        else
            appResources.MergedDictionaries.Insert(0, newDict);
    }
}
