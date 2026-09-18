using System.Windows;

namespace TestControllerGrpc.Views.Behaviors;

/// <summary>Width bands from the UI spec (G-8).</summary>
public enum Breakpoint
{
    Narrow,
    Compact,
    Standard,
    Wide,
}

/// <summary>
/// Attached behavior that classifies an element's width into a <see cref="Breakpoint"/> band so views
/// can adapt with triggers instead of code-behind. WPF has no CSS media queries, and binding directly to
/// ActualWidth forces every view to repeat the same threshold arithmetic.
/// </summary>
public static class LayoutBreakpoint
{
    public const double WideMin = 1600;
    public const double StandardMin = 1366;
    public const double CompactMin = 1024;

    public static Breakpoint Classify(double width) => width switch
    {
        >= WideMin => Breakpoint.Wide,
        >= StandardMin => Breakpoint.Standard,
        >= CompactMin => Breakpoint.Compact,
        _ => Breakpoint.Narrow,
    };

    /// <summary>
    /// Which side panes survive at a given width, given what the user asked for. The agent pane yields
    /// first and the tree second; the log answers to the user's own collapse toggle. Intent is never
    /// widened - a pane the user unpinned stays unpinned however much room appears.
    /// </summary>
    public static (bool Tree, bool Agent, bool Log) PanesFor(
        (bool Tree, bool Agent, bool Log) intent, Breakpoint band) => band switch
        {
            Breakpoint.Narrow => (false, false, intent.Log),
            Breakpoint.Compact => (intent.Tree, false, intent.Log),
            _ => intent,
        };

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(LayoutBreakpoint),
            new UIPropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(FrameworkElement element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(FrameworkElement element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    /// <summary>Current band. Inherits, so nested elements can trigger on an ancestor's width.</summary>
    public static readonly DependencyProperty CurrentProperty =
        DependencyProperty.RegisterAttached(
            "Current",
            typeof(Breakpoint),
            typeof(LayoutBreakpoint),
            new FrameworkPropertyMetadata(Breakpoint.Wide, FrameworkPropertyMetadataOptions.Inherits));

    public static Breakpoint GetCurrent(DependencyObject element) =>
        (Breakpoint)element.GetValue(CurrentProperty);

    public static void SetCurrent(DependencyObject element, Breakpoint value) =>
        element.SetValue(CurrentProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.SizeChanged += OnSizeChanged;
            element.Loaded += OnLoaded;
            Apply(element);
        }
        else
        {
            element.SizeChanged -= OnSizeChanged;
            element.Loaded -= OnLoaded;
        }
    }

    // ActualWidth is 0 until the first layout pass, which would classify everything as Narrow.
    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            Apply((FrameworkElement)sender);
    }

    private static void Apply(FrameworkElement element)
    {
        if (element.ActualWidth <= 0)
            return;

        var band = Classify(element.ActualWidth);
        if (GetCurrent(element) != band)
            SetCurrent(element, band);
    }
}
