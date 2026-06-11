using System.Globalization;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels.Login;

/// <summary>Returns "Signing in…" when true, "Sign In" when false.</summary>
public sealed class BoolToSignInTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Signing in…" : "Sign In";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
