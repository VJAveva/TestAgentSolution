using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TestControllerGrpc.ViewModels.Admin;

namespace TestControllerGrpc.Views.Admin;

public partial class AddUserDialog : Window
{
    private readonly AddUserDialogViewModel _viewModel;

    /// <summary>The generated password to display after creation. Null if cancelled.</summary>
    public string? GeneratedPassword { get; private set; }

    public AddUserDialog()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<AddUserDialogViewModel>();
        DataContext = _viewModel;

        _viewModel.UserCreated += OnUserCreated;
    }

    private void OnUserCreated(string generatedPassword)
    {
        GeneratedPassword = generatedPassword;
        _viewModel.UserCreated -= OnUserCreated;
        DialogResult = true;
        Close();
    }
}
