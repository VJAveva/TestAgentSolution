using System.Windows;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class DeleteUserConfirmDialog : Window
{
    public DeleteUserConfirmDialog(string username, int assignmentCount)
    {
        InitializeComponent();
        DataContext = new DeleteUserConfirmDialogViewModel(username, assignmentCount);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
