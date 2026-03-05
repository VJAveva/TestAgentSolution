using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TestControllerGrpc.Helpers;

/// <summary>
/// Lightweight WPF control that renders an SVG icon at a given size.
/// Supports DynamicResource binding for <see cref="Foreground"/> so icons
/// automatically re-render when the theme changes.
/// <para>
/// Usage: <c>&lt;helpers:SvgIconControl IconName="execute" IconSize="24"
///            Foreground="{DynamicResource TextP}" /&gt;</c>
/// </para>
/// </summary>
public sealed class SvgIconControl : ContentControl
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        SnapsToDevicePixels = true,
    };

    public static readonly DependencyProperty IconNameProperty =
        DependencyProperty.Register(nameof(IconName), typeof(string), typeof(SvgIconControl),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnIconPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(SvgIconControl),
            new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnIconPropertyChanged));

    /// <summary>SVG file name without extension (e.g. "execute").</summary>
    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    /// <summary>Desired pixel size (width &amp; height).</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public SvgIconControl()
    {
        Focusable = false;
        IsTabStop = false;
        Content = _image;
        Loaded += (_, _) => RefreshIcon();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty && IsLoaded)
            RefreshIcon();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        return new Size(IconSize, IconSize);
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (SvgIconControl)d;
        if (ctrl.IsLoaded)
            ctrl.RefreshIcon();
    }

    private void RefreshIcon()
    {
        if (string.IsNullOrEmpty(IconName)) return;

        _image.Source = SvgIconHelper.GetImageSource(IconName, IconSize, Foreground);
        _image.Width = IconSize;
        _image.Height = IconSize;
    }
}
