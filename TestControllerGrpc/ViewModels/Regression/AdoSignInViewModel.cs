using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Regression;

/// <summary>View-model for the AVEVA Azure DevOps sign-in dialog (interactive Microsoft/Entra auth).</summary>
public sealed partial class AdoSignInViewModel : ObservableObject
{
    private readonly IInteractiveAdoAuthenticator _auth;
    private readonly RetrievalIndexBuilder _indexBuilder;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _rebuildCts;

    public AdoSignInViewModel(IInteractiveAdoAuthenticator auth, RetrievalIndexBuilder indexBuilder, IAppLogger logger)
    {
        _auth = auth;
        _indexBuilder = indexBuilder;
        _logger = logger;
        RefreshState();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRebuildCommand))]
    private bool _isRebuilding;

    [ObservableProperty] private string _accountText = "Not signed in";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _rebuildStatusText = "";

    /// <summary>Blocks dialog close mid-rebuild so a cancelled-by-close build can't be left half-written.</summary>
    public bool CanCloseDialog => !IsRebuilding;

    partial void OnIsRebuildingChanged(bool value) => OnPropertyChanged(nameof(CanCloseDialog));

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

    private bool CanRebuildIndex => IsSignedIn && !IsBusy && !IsRebuilding;

    /// <summary>
    /// Full index rebuild driven by the signed-in user's delegated token. This is the recovery path for when
    /// the writer host's own credential is unusable; it writes the machine-wide index under %ProgramData%,
    /// so it must be run ON the host that serves the index.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRebuildIndex))]
    private async Task RebuildIndex()
    {
        _rebuildCts = new CancellationTokenSource();
        IsRebuilding = true;
        RebuildStatusText = "Starting full rebuild\u2026 this can take a long time.";
        try
        {
            var progress = new Progress<ImpactMappingProgress>(p => RebuildStatusText = p.Message);
            IndexBuildResult result = await _indexBuilder.BuildAsync(fullRebuild: true, progress, _rebuildCts.Token);
            RebuildStatusText =
                $"Rebuilt in {result.Elapsed:hh\\:mm\\:ss} \u00b7 {result.DocumentsIndexed} indexed, {result.DocumentsSkipped} skipped " +
$"({result.TestCasesSeen} test cases, {result.FeaturesSeen} features).";
            _logger.Info("ImpactIndex", $"Interactive rebuild by {_auth.SignedInUser}: {RebuildStatusText}");
        }
        catch (OperationCanceledException)
        {
            RebuildStatusText = "Cancelled. Documents written so far are kept; run it again to finish.";
            _logger.Warn("ImpactIndex", "Interactive index rebuild cancelled by the user.");
        }
        catch (Exception ex)
        {
            RebuildStatusText = $"Rebuild failed: {ex.Message}";
            _logger.Error("ImpactIndex", "Interactive index rebuild failed.", ex);
        }
        finally
        {
            IsRebuilding = false;
            _rebuildCts?.Dispose();
            _rebuildCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRebuilding))]
    private void CancelRebuild() => _rebuildCts?.Cancel();
}
