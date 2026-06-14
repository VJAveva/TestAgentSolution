using System.Windows;
using System.Windows.Controls;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class UserListView : UserControl
{
    public UserListView()
    {
        InitializeComponent();
    }

    private void RoleFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && DataContext is UserManagementViewModel vm)
        {
            vm.RoleFilter = tag;
        }
    }
}
