using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// Dialog ViewModel for displaying a reset password result.
/// Shows the generated password ONCE. Cannot be dismissed until copied or explicitly cancelled.
/// </summary>
public sealed partial class ResetPasswordDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private bool _hasCopied;
    [ObservableProperty] private bool _explicitlyCancelled;

    /// <summary>True when the dialog can be closed (password copied or explicitly cancelled).</summary>
    public bool CanClose => HasCopied || ExplicitlyCancelled;

    public ResetPasswordDialogViewModel(string username, string newPassword)
    {
        _username = username;
        _newPassword = newPassword;
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        System.Windows.Clipboard.SetText(NewPassword);
        HasCopied = true;
    }

    [RelayCommand]
    private void ExplicitCancel()
    {
        ExplicitlyCancelled = true;
    }

    partial void OnHasCopiedChanged(bool value) => OnPropertyChanged(nameof(CanClose));
    partial void OnExplicitlyCancelledChanged(bool value) => OnPropertyChanged(nameof(CanClose));
}
