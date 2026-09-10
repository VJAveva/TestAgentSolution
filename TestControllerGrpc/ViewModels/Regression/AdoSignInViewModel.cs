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

    public AdoSignInViewModel(IInteractiveAdoAuthenticator auth, IImpactIndexRebuildService rebuild, IAppLogger logger)
    {
        _auth = auth;
        _logger = logger;
        Rebuild = rebuild;
        RefreshState();
    }

    /// <summary>Bound directly by the view; lives outside this VM so a rebuild survives the dialog closing.</summary>
    public IImpactIndexRebuildService Rebuild { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    private bool _isSignedIn;

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

    private bool CanRebuildIndex => IsSignedIn && !IsBusy;

    /// <summary>
    /// Full index rebuild driven by the signed-in user's delegated token. This is the recovery path for when
    /// the writer host's own credential is unusable; it writes the machine-wide index under %ProgramData%,
    /// so it must be run ON the host that serves the index.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRebuildIndex))]
    private void RebuildIndex() => Rebuild.Start();

    [RelayCommand]
    private void CancelRebuild() => Rebuild.Cancel();
}
