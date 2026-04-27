using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TestControllerGrpc.ViewModels.Execution;

namespace TestControllerGrpc.Views;

public partial class TimelineWindow : Window
{
    private TimelineVM? _timeline;

    public TimelineWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public ExecutionDashboardVM? Dashboard => (DataContext as TimelineWindowContext)?.Dashboard;
    public TimelineVM? Timeline => (DataContext as TimelineWindowContext)?.Timeline;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is TimelineWindowContext ctx)
        {
            _timeline = ctx.Timeline;
            _timeline.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(TimelineVM.CursorSeconds) or nameof(TimelineVM.TotalSeconds))
                    UpdateTimeAxis();
            };
        }
    }

    private void UpdateTimeAxis()
    {
        if (_timeline == null) return;

        TimeAxis.Children.Clear();
        var width = TimeAxis.ActualWidth > 0 ? TimeAxis.ActualWidth : 1000;
        var totalSec = _timeline.TotalSeconds;
        if (totalSec <= 0) return;

        // Draw tick marks every 15 seconds
        for (double t = 0; t <= totalSec; t += 15)
        {
            var x = t / totalSec * width;
            var line = new Line
            {
                X1 = x, Y1 = 18, X2 = x, Y2 = 24,
                Stroke = FindResource("TextDim") as Brush,
                StrokeThickness = 0.5
            };
            TimeAxis.Children.Add(line);

            var minutes = (int)(t / 60);
            var seconds = (int)(t % 60);
            var label = new TextBlock
            {
                Text = $"{minutes}:{seconds:D2}",
                FontSize = 9,
                Foreground = FindResource("TextDim") as Brush
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 2);
            TimeAxis.Children.Add(label);
        }
    }

    /// <summary>
    /// Recalculates Canvas.Left and Width for each bar when the canvas resizes.
    /// </summary>
    private void BarCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Canvas canvas || _timeline == null) return;
        var width = canvas.ActualWidth;
        var totalSec = _timeline.TotalSeconds;
        if (totalSec <= 0 || width <= 0) return;

        // Find the ItemsControl inside this canvas
        foreach (var child in canvas.Children)
        {
            if (child is ItemsControl ic)
            {
                for (int i = 0; i < ic.Items.Count; i++)
                {
                    if (ic.Items[i] is TimelineBar bar &&
                        ic.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                    {
                        var left = bar.OffsetSeconds / totalSec * width;
                        var barWidth = Math.Max(4, bar.DurationSeconds / totalSec * width);
                        Canvas.SetLeft(cp, left);
                        cp.Width = barWidth;
                    }
                }
            }

            // Update cursor line
            if (child is Line line && line.Name == "CursorLine")
            {
                var cx = _timeline.CursorSeconds / totalSec * width;
                Canvas.SetLeft(line, cx);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timeline?.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>Bundled context for the timeline window's DataContext.</summary>
public sealed class TimelineWindowContext
{
    public required ExecutionDashboardVM Dashboard { get; init; }
    public required TimelineVM Timeline { get; init; }
}
