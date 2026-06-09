using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class HealthMetricsStrip : UserControl
{
    private Storyboard? _pulseStoryboard;

    public HealthMetricsStrip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is HealthMetricsVM oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is HealthMetricsVM newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HealthMetricsVM.IsCritical))
        {
            var vm = (HealthMetricsVM)sender!;
            if (vm.IsCritical)
                StartPulse();
            else
                StopPulse();
        }
    }

    private void StartPulse()
    {
        _pulseStoryboard ??= (Storyboard)FindResource("PulseAnimation");
        _pulseStoryboard.Begin(this, true);
    }

    private void StopPulse()
    {
        _pulseStoryboard?.Stop(this);
        HealthDot.Opacity = 1;
    }
}

/// <summary>Converts HealthLevel enum to a SolidColorBrush (Green/Amber/Red).</summary>
public sealed class HealthLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xFF, 0xA7, 0x26));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xF4, 0x43, 0x36));

    static HealthLevelToBrushConverter()
    {
        Green.Freeze(); Amber.Freeze(); Red.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is HealthLevel level ? level switch
        {
            HealthLevel.Amber => Amber,
            HealthLevel.Red => Red,
            _ => Green,
        } : Green;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a 0-100 percent value to a width (max 40px for the mini bar).</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = System.Convert.ToDouble(value);
        return Math.Max(1, Math.Min(40, percent / 100.0 * 40));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts network latency (ms) to a bar width (0-300ms scale, max 40px).</summary>
public sealed class LatencyToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ms = System.Convert.ToDouble(value);
        return Math.Max(1, Math.Min(40, ms / 300.0 * 40));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
