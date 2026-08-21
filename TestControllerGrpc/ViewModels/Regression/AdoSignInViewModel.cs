using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Regression;

/// <summary>View-model for the AVEVA Azure DevOps sign-in dialog (interactive Microsoft/Entra auth).</summary>
public sealed partial class AdoSignInViewModel : ObservableObject
{
    private readonly IInteractiveAdoAuthenticator _auth;
    private readonly IAppLogger _logger;

    public AdoSignInViewModel(IInteractiveAdoAuthenticator auth, IAppLogger logger)
    {
        _auth = auth;
        _logger = logger;
        RefreshState();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountText = "Not signed in";
    [ObservableProperty] private string _statusText = "";

    /// <summary>Raised after a successful sign-in so the caller can reload live data.</summary>
    public event EventHandler? SignedIn;

    private bool CanInteract => !IsBusy;

    private void RefreshState()
    {
        IsSignedIn = _auth.IsSignedIn;
        AccountText = _auth.SignedInUser ?? "Not signed in";
        StatusText = _auth.IsSignedIn
            ? $"Signed in \u00b7 token valid until {_auth.TokenExpiresOn?.LocalDateTime:g}"
            : _auth.IsAvailable
                ? "Sign in with your AVEVA Microsoft account to load live Azure DevOps data."
                : "Interactive sign-in is not enabled (set Ado:AuthMode to \u201cInteractive\u201d in appsettings).";
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SignIn()
    {
        IsBusy = true;
        StatusText = "Opening Microsoft sign-in\u2026 complete it in your browser.";
        try
        {
            var result = await _auth.SignInAsync(CancellationToken.None);
            if (result.Success)
            {
                RefreshState();
                SignedIn?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                RefreshState();
                StatusText = $"Sign-in failed: {result.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SignOut()
    {
        IsBusy = true;
        try
        {
            await _auth.SignOutAsync(CancellationToken.None);
            RefreshState();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
