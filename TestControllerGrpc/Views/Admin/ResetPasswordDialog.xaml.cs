using System.ComponentModel;
using System.Windows;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class ResetPasswordDialog : Window
{
    private readonly ResetPasswordDialogViewModel _viewModel;

    public ResetPasswordDialog(string username, string newPassword)
    {
        InitializeComponent();
        _viewModel = new ResetPasswordDialogViewModel(username, newPassword);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResetPasswordDialogViewModel.CanClose) && _viewModel.CanClose)
        {
            Dispatcher.InvokeAsync(() =>
            {
                DialogResult = !_viewModel.ExplicitlyCancelled;
            });
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Cannot close until password is copied or explicitly cancelled
        if (!_viewModel.CanClose)
        {
            e.Cancel = true;
            MessageBox.Show(
                "The generated password will be lost. Click 'I've noted it' to dismiss, or copy it first.",
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        base.OnClosing(e);
    }
}
