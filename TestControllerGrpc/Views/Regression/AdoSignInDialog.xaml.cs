using System.Windows;
using TestControllerGrpc.ViewModels.Regression;

namespace TestControllerGrpc.Views.Regression;

public partial class AdoSignInDialog : Window
{
    public AdoSignInDialog(AdoSignInViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
