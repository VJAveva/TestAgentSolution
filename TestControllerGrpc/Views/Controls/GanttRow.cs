using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Views.Controls;

/// <summary>
/// Custom WPF <see cref="Control"/> that renders a single agent's Gantt row in
/// the Timeline tab. Inherits from <see cref="Control"/> (not <c>UserControl</c>)
/// so layout cost is one DrawingContext pass instead of an ItemsControl with
/// per-bar containers.
///
/// Architecture choices:
///   ? <see cref="ActionsProperty"/> takes any <see cref="IEnumerable"/> of
///     <see cref="ActionPillVM"/> ? the same VM type used by the Pipeline tab,
///     so models stay shared across views.
///   ? Hooks <see cref="INotifyCollectionChanged"/> on the bound collection
///     and <see cref="INotifyPropertyChanged"/> on each item, so any status,
///     progress, start, or duration mutation triggers <see cref="UIElement.InvalidateVisual"/>.
///   ? Falls back to a 90-minute window if nothing has started yet, so an
///     empty lane still has a sensible scale.
///   ? Bars use the same accent palette declared in
///     <c>ExecutionDashboardStyles.xaml</c> (resolved via FindResource so
///     theme switches are picked up automatically).
/// </summary>
public sealed class GanttRow : Control
{
    static GanttRow()
    {
        // Apply default style key so the control can be themed if a Style is added later.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(GanttRow),
            new FrameworkPropertyMetadata(typeof(GanttRow)));
    }

    public GanttRow()
    {
        // Sensible defaults so the control is visible without an explicit Style.
        Background = Brushes.Transparent;
        MinHeight = 28;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    /// <summary>The collection of <see cref="ActionPillVM"/> instances to render as bars.</summary>
    public IEnumerable? Actions
    {
        get => (IEnumerable?)GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(IEnumerable),
        typeof(GanttRow),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnActionsChanged));

    private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GanttRow row) row.RewireCollection(e.OldValue as IEnumerable, e.NewValue as IEnumerable);
    }

    private void RewireCollection(IEnumerable? oldColl, IEnumerable? newColl)
    {
        if (oldColl is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= OnCollectionChanged;
        DetachItemHandlers(oldColl);

        if (newColl is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += OnCollectionChanged;
        AttachItemHandlers(newColl);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged npc) npc.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged npc) npc.PropertyChanged += OnItemPropertyChanged;

        InvalidateVisual();
    }

    private void AttachItemHandlers(IEnumerable? coll)
    {
        if (coll == null) return;
        foreach (var item in coll)
            if (item is INotifyPropertyChanged npc) npc.PropertyChanged += OnItemPropertyChanged;
    }

    private void DetachItemHandlers(IEnumerable? coll)
    {
        if (coll == null) return;
        foreach (var item in coll)
            if (item is INotifyPropertyChanged npc) npc.PropertyChanged -= OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Bar geometry depends on Status / OffsetSeconds / DurationSeconds / ActionTag.
        switch (e.PropertyName)
        {
            case nameof(TimelineBar.Status):
            case nameof(TimelineBar.OffsetSeconds):
            case nameof(TimelineBar.DurationSeconds):
            case nameof(TimelineBar.ActionTag):
                Dispatcher.BeginInvoke(InvalidateVisual);
                break;
        }
    }

    /// <summary>
    /// Renders all bars proportionally between the earliest StartedUtc and
    /// the latest end time (StartedUtc + DurationSeconds, falling back to
    /// "now" for running actions).
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        if (Background != null)
            dc.DrawRectangle(Background, null, bounds);

        var bars = (Actions as IEnumerable)?
            .OfType<TimelineBar>()
            .ToList() ?? new List<TimelineBar>();
        if (bars.Count == 0) return;

        // Compute time window: bars are positioned by OffsetSeconds (relative
        // to a global session start), so the window is min(offset) .. max(offset+duration).
        var minOffset = bars.Min(b => b.OffsetSeconds);
        var maxEnd = bars.Max(b => b.OffsetSeconds +
                                   (b.DurationSeconds > 0
                                       ? b.DurationSeconds
                                       : (b.Status == "Running" ? 30 : 1)));
        var windowSeconds = maxEnd - minOffset;
        if (windowSeconds < 60) windowSeconds = 5400; // 90-minute fallback

        // Brush palette from app resources ? falls back to hard-coded if theme is missing.
        var blue = ResolveBrush("AccentBlue", "#FF4A9EFF");
        var green = ResolveBrush("AccentGreen", "#FF3DDC84");
        var red = ResolveBrush("AccentRed", "#FFFF5C6C");
        var grey = ResolveBrush("BgChip", "#FF2A313C");
        var border = ResolveBrush("BorderStrong", "#FF3A4250");

        const double barHeight = 18;
        var top = (ActualHeight - barHeight) / 2;
        var radius = 4.0;

        var typeface = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var bar in bars.OrderBy(b => b.OffsetSeconds))
        {
            var startOffset = bar.OffsetSeconds - minOffset;
            var dur = bar.DurationSeconds > 0 ? bar.DurationSeconds : 1;

            var x = startOffset / windowSeconds * ActualWidth;
            var w = Math.Max(4, dur / windowSeconds * ActualWidth);
            if (x + w > ActualWidth) w = Math.Max(4, ActualWidth - x);

            var fill = bar.Status switch
            {
                "Running" => blue,
                "Success" => green,
                "Failed" => red,
                _ => grey,
            };

            var rect = new Rect(x, top, w, barHeight);
            dc.DrawRoundedRectangle(fill, new Pen(border, 0.5), rect, radius, radius);

            if (w >= 32 && !string.IsNullOrEmpty(bar.ActionTag))
            {
                // Sanitize: strip non-BMP characters (surrogate pairs) that can
                // cause infinite recursion in TextShaping.dll / DWrite.dll when
                // the default font lacks the glyph.
                var safeTag = SanitizeForRendering(bar.ActionTag);
                if (string.IsNullOrEmpty(safeTag)) continue;

                var ft = new FormattedText(
                    safeTag, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, 10, Brushes.White, dpi)
                {
                    MaxTextWidth = Math.Max(1, w - 8),
                    MaxTextHeight = barHeight,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(ft, new Point(x + 4, top + (barHeight - ft.Height) / 2));
            }
        }
    }

    /// <summary>
    /// Strips characters outside the Basic Multilingual Plane (surrogate pairs)
    /// that can cause stack overflow in DWrite / TextShaping.dll when the active
    /// font lacks the glyph.
    /// </summary>
    private static string SanitizeForRendering(string text)
    {
        // Fast path: most strings are pure BMP.
        if (!HasSurrogatePairs(text)) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append('?'); // Replace non-BMP char with safe placeholder
                i++; // Skip low surrogate
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    private static bool HasSurrogatePairs(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (char.IsHighSurrogate(text[i])) return true;
        return false;
    }

    private Brush ResolveBrush(string resourceKey, string fallbackHex)
    {
        if (TryFindResource(resourceKey) is Brush b) return b;
        return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
    }
}
