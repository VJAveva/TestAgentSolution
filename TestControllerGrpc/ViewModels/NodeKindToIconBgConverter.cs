using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Converts a NodeKind string to the appropriate icon background DynamicResource brush key.
/// Maps: WatchList/TemplateList ? IconBgFolder, WatchItem ? IconBgWatch, etc.
/// Falls back to IconBgAction if the key is not found.
/// </summary>
public sealed class NodeKindToIconBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as string ?? "";
        var resourceKey = kind switch
        {
            "WatchList" or "TemplateList" => "IconBgFolder",
            "WatchItem" => "IconBgWatch",
            "Event" => "IconBgEvent",
            "Template" => "IconBgTemplate",
            "ActionGroup" => "IconBgGroup",
            "Action" => "IconBgAction",
            "Initialize" => "IconBgInit",
            "Ref" => "IconBgRef",
            _ => "IconBgAction"
        };

        return Application.Current?.TryFindResource(resourceKey) as Brush
            ?? new SolidColorBrush(Color.FromArgb(0x33, 0x89, 0xB4, 0xFA));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
