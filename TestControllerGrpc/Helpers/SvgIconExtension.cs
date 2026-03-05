using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace TestControllerGrpc.Helpers;

/// <summary>
/// XAML markup extension that loads an SVG icon by name at a given size.
/// <para>Usage: <c>&lt;Button Content="{helpers:SvgIcon Name=execute, Size=24}" /&gt;</c></para>
/// </summary>
//[MarkupExtensionReturnType(typeof(Image))]
public sealed class SvgIconExtension : MarkupExtension
{
    /// <summary>SVG file name without extension (e.g. "execute").</summary>
    public string Name { get; set; } = "";

    /// <summary>Desired pixel size (width &amp; height).</summary>
    public double Size { get; set; } = 24;

    /// <summary>
    /// Optional foreground brush used to recolor the icon.
    /// </summary>
    public Brush? Foreground { get; set; }

    public SvgIconExtension() { }

    public SvgIconExtension(string name) => Name = name;

    public SvgIconExtension(string name, double size)
    {
        Name = name;
        Size = size;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return SvgIconHelper.LoadSvgIcon(Name, Size, Foreground);
    }
}
