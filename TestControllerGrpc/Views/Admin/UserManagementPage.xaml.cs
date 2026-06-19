using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class UserManagementPage : UserControl
{
    private readonly UserManagementViewModel _vm;

    public UserManagementPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<UserManagementViewModel>();
        DataContext = _vm;

        _vm.RequestOpenAddUser += OnOpenAddUser;
        _vm.RequestOpenAssignPipelines += OnOpenAssignPipelines;
        _vm.RequestOpenResetPassword += OnOpenResetPassword;
        _vm.RequestOpenDeleteUser += OnOpenDeleteUser;
    }

    private void OnOpenAddUser()
    {
        try
        {
            var dialog = new AddUserDialog();
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                // Show generated password
                if (dialog.GeneratedPassword is { } pwd)
                {
                    var pwdDialog = new ResetPasswordDialog(
                        App.Services.GetRequiredService<UserManagementViewModel>().SearchText ?? "New user", pwd);
                    pwdDialog.Owner = Window.GetWindow(this);
                    pwdDialog.ShowDialog();
                }
                _ = _vm.LoadUsersCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<IAppLogger>().Error("UserMgmt", "Failed to open AddUser dialog", ex);
        }
    }

    private async void OnOpenAssignPipelines()
    {
        if (_vm.SelectedUser is null) return;
        try
        {
            var vocabMonitor = App.Services.GetRequiredService<IVocabularyMonitor>();
            var allTags = vocabMonitor.CurrentConfig?.WatchItems
                .Select(w => w.Tag)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList() ?? [];

            var dialog = new AssignPipelinesDialog();
            dialog.Owner = Window.GetWindow(this);
            await dialog.InitializeAsync(_vm.SelectedUser.UserId, _vm.SelectedUser.Username, _vm.SelectedUser.Role, allTags);

            if (dialog.ShowDialog() == true)
            {
                _ = _vm.LoadUsersCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<IAppLogger>().Error("UserMgmt", "Failed to open AssignPipelines dialog", ex);
        }
    }

    private async void OnOpenResetPassword()
    {
        if (_vm.SelectedUser is null) return;
        try
        {
            var client = App.Services.GetRequiredService<UserManagementClient>();
            var logger = App.Services.GetRequiredService<IAppLogger>();

            var (newPassword, error) = await client.ResetPasswordAsync(_vm.SelectedUser.UserId);
            if (newPassword is not null)
            {
                logger.Info("UserMgmt", $"Reset password for {_vm.SelectedUser.Username}");
                var dialog = new ResetPasswordDialog(_vm.SelectedUser.Username, newPassword);
                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();
                _ = _vm.LoadUsersCommand.ExecuteAsync(null);
            }
            else
            {
                _vm.ErrorMessage = error ?? "Reset password failed";
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<IAppLogger>().Error("UserMgmt", "ResetPassword failed", ex);
            _vm.ErrorMessage = "Connection error";
        }
    }

    private async void OnOpenDeleteUser()
    {
        if (_vm.SelectedUser is null) return;
        try
        {
            var dialog = new DeleteUserConfirmDialog(
                _vm.SelectedUser.Username, _vm.SelectedUser.PipelineCount);
            dialog.Owner = Window.GetWindow(this);

            if (dialog.ShowDialog() == true)
            {
                var client = App.Services.GetRequiredService<UserManagementClient>();
                var (success, error) = await client.DeleteUserAsync(_vm.SelectedUser.UserId);
                if (success)
                {
                    App.Services.GetRequiredService<IAppLogger>()
                        .Info("UserMgmt", $"Deleted user {_vm.SelectedUser.Username}");
                    _vm.SelectedUser = null;
                    _ = _vm.LoadUsersCommand.ExecuteAsync(null);
                }
                else
                {
                    _vm.ErrorMessage = error ?? "Delete failed";
                }
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<IAppLogger>().Error("UserMgmt", "DeleteUser failed", ex);
            _vm.ErrorMessage = "Connection error";
        }
    }
}
