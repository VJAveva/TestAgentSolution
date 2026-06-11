using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class UserManagementPage : UserControl
{
    public UserManagementPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<UserManagementViewModel>();
    }
}
