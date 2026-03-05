using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace TestControllerGrpc.Helpers;

/// <summary>
/// Loads SVG icon files from the Icons folder, renders them at a requested size,
/// and caches the result so each icon+size+color combination is only rasterized once.
/// </summary>
public static class SvgIconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource> _cache = new();
    private static readonly string _iconsFolder =
        Path.Combine(AppContext.BaseDirectory, "Icons");

    private static readonly Regex StrokeAttrRegex =
        new(@"stroke=""(?!none|white)([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex FillAttrRegex =
        new(@"fill=""(?!none|white)([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Loads an SVG icon by name, renders it at the specified pixel size, and
    /// optionally recolors all strokes/fills to <paramref name="foreground"/>.
    /// Returns a ready-to-use <see cref="Image"/> control.
    /// </summary>
    public static Image LoadSvgIcon(string iconName, double size, Brush? foreground = null)
    {
        var source = GetImageSource(iconName, size, foreground);
        return new Image
        {
            Source = source,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
    }

    /// <summary>
    /// Returns a cached <see cref="ImageSource"/> for the given icon, size, and color.
    /// </summary>
    public static ImageSource GetImageSource(string iconName, double size, Brush? foreground = null)
    {
        var dpi = GetSystemDpi();
        var colorHex = foreground is SolidColorBrush scb
            ? scb.Color.ToString()
            : "default";
        var key = $"{iconName}_{size}_{dpi}_{colorHex}";

        return _cache.GetOrAdd(key, _ => RenderSvg(iconName, size, dpi, foreground));
    }

    private static ImageSource RenderSvg(string iconName, double logicalSize, double dpi, Brush? foreground)
    {
        var path = Path.Combine(_iconsFolder, $"{iconName}.svg");
        if (!File.Exists(path))
            return CreateFallback(logicalSize);

        var svgText = File.ReadAllText(path);

        if (foreground is SolidColorBrush scb)
        {
            var hex = $"#{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}";
            svgText = StrokeAttrRegex.Replace(svgText, $"stroke=\"{hex}\"");
            svgText = FillAttrRegex.Replace(svgText, $"fill=\"{hex}\"");
        }

        using var svg = new SKSvg();
        svg.FromSvg(svgText);
        if (svg.Picture is null)
            return CreateFallback(logicalSize);

        double scaleFactor = dpi / 96.0;
        int pixelSize = Math.Max(1, (int)Math.Ceiling(logicalSize * scaleFactor));

        var bounds = svg.Picture.CullRect;
        float svgScale = pixelSize / Math.Max(bounds.Width, bounds.Height);

        var info = new SKImageInfo(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
            return CreateFallback(logicalSize);

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        float dx = (pixelSize - bounds.Width * svgScale) / 2f;
        float dy = (pixelSize - bounds.Height * svgScale) / 2f;
        canvas.Translate(dx, dy);
        canvas.Scale(svgScale);
        canvas.DrawPicture(svg.Picture);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var bitmap = new BitmapImage();
        using (var ms = new MemoryStream(data.ToArray()))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.DecodePixelWidth = pixelSize;
            bitmap.DecodePixelHeight = pixelSize;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        var drawingImage = new DrawingImage(
            new ImageDrawing(bitmap, new Rect(0, 0, logicalSize, logicalSize)));
        drawingImage.Freeze();
        return drawingImage;
    }

    private static ImageSource CreateFallback(double logicalSize)
    {
        int px = Math.Max(1, (int)Math.Ceiling(logicalSize));
        var fallback = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        fallback.Freeze();
        return fallback;
    }

    private static double GetSystemDpi()
    {
        try
        {
            var window = Application.Current?.MainWindow;
            if (window is not null)
            {
                var source = PresentationSource.FromVisual(window);
                if (source?.CompositionTarget is { } target)
                    return 96.0 * target.TransformToDevice.M11;
            }
        }
        catch (InvalidOperationException)
        {
            // Window not yet fully initialized — fall through to default
        }

        return 96.0;
    }

    /// <summary>Clears the icon cache (e.g. after a theme change).</summary>
    public static void ClearCache() => _cache.Clear();
}
