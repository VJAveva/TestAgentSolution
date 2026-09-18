using System.Globalization;
using System.Windows.Data;

namespace TestControllerGrpc.ViewModels.Login;

/// <summary>
/// Button caption for the forced password-change window. Mirrors <see cref="BoolToSignInTextConverter"/>;
/// ChangePasswordPage.xaml referenced this key without anything declaring it, which threw
/// XamlParseException and made the window fail to open.
/// </summary>
public sealed class BoolToChangePasswordTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Changing password…" : "Change password";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
