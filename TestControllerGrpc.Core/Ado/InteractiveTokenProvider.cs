using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>Outcome of an interactive Azure DevOps sign-in.</summary>
public sealed record AdoSignInResult(bool Success, string? User, DateTimeOffset? ExpiresOn, string? Error);

/// <summary>
/// Drives interactive Microsoft (Entra) sign-in for Azure DevOps from the WPF UI. The acquired token is
/// cached and silently refreshed, so — unlike a PAT — it does not need manual rotation.
/// </summary>
public interface IInteractiveAdoAuthenticator
{
    /// <summary>False when the app isn't configured for interactive auth (the UI hides the sign-in affordance).</summary>
    bool IsAvailable { get; }
    bool IsSignedIn { get; }
    string? SignedInUser { get; }
    DateTimeOffset? TokenExpiresOn { get; }

    /// <summary>Interactively sign in (opens the Microsoft sign-in in the browser).</summary>
    Task<AdoSignInResult> SignInAsync(CancellationToken ct);

    /// <summary>Forget the cached account; the next data load requires a fresh sign-in.</summary>
    Task SignOutAsync(CancellationToken ct);

    /// <summary>Silently restore a previous session (no prompt). True if a valid token is available.</summary>
    Task<bool> RestoreAsync(CancellationToken ct);
}

/// <summary>
/// Delegated interactive auth for the WPF host (ADR-03) — the signed-in user's own Entra identity.
/// A persisted token cache + <see cref="AuthenticationRecord"/> means the browser prompt appears only when a
/// fresh sign-in is genuinely required; otherwise the token is refreshed silently. Background ADO calls never
/// pop the browser (<see cref="InteractiveBrowserCredentialOptions.DisableAutomaticAuthentication"/>) — the UI
/// drives the interactive prompt via <see cref="SignInAsync"/>.
/// </summary>
public sealed class InteractiveTokenProvider : IAdoTokenProvider, IInteractiveAdoAuthenticator
{
    private static readonly string[] Scopes = [$"{AdoOptions.AdoResourceId}/.default"];
    private static readonly TokenRequestContext Request = new(Scopes);

    // Microsoft Azure CLI public client id — a first-party app pre-authorized for Azure DevOps, used as the
    // default so interactive sign-in works without a bespoke Entra app registration. Override via Ado:ClientId.
    private const string DefaultClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;
    private readonly string _recordPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InteractiveBrowserCredential? _credential;
    private AuthenticationRecord? _record;
    private DateTimeOffset? _expiresOn;

    public InteractiveTokenProvider(IOptions<AdoOptions> options, IAppLogger logger)
    {
        _options = options.Value;
        _logger = logger;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestAgentSolution");
        Directory.CreateDirectory(dir);
        _recordPath = Path.Combine(dir, "ado-auth.bin");
    }

    public bool IsAvailable => true;
    public bool IsSignedIn => _record is not null;
    public string? SignedInUser => _record?.Username;
    public DateTimeOffset? TokenExpiresOn => _expiresOn;

    // Fall back to well-known values so sign-in works out of the box; a specific org app can override via config.
    private string TenantId => string.IsNullOrWhiteSpace(_options.TenantId) ? "organizations" : _options.TenantId!;
    private string ClientId => string.IsNullOrWhiteSpace(_options.ClientId) ? DefaultClientId : _options.ClientId!;

    private InteractiveBrowserCredential Credential() =>
        _credential ??= new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = TenantId,
            ClientId = ClientId,
            DisableAutomaticAuthentication = true,
            AuthenticationRecord = _record,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "TestAgentSolution.Ado" },
        });

    public async Task<bool> RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_record is null && File.Exists(_recordPath))
            {
                await using var fs = File.OpenRead(_recordPath);
                _record = await AuthenticationRecord.DeserializeAsync(fs, ct);
                _credential = null; // rebuild the credential bound to the restored account
            }
            if (_record is null)
                return false;

            var token = await Credential().GetTokenAsync(Request, ct);
            _expiresOn = token.ExpiresOn;
            _logger.Info("Ado", $"Restored ADO session for {SignedInUser} (token valid until {_expiresOn:u}).");
            return true;
        }
        catch (AuthenticationRequiredException)
        {
            return false; // cached refresh token expired — a fresh interactive sign-in is needed
        }
        catch (Exception ex)
        {
            _logger.Warn("Ado", $"Silent ADO token restore failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdoSignInResult> SignInAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _credential = null; // start from clean options
            var credential = Credential();
            _record = await credential.AuthenticateAsync(Request, ct);
            var token = await credential.GetTokenAsync(Request, ct);
            _expiresOn = token.ExpiresOn;
            await PersistRecordAsync(ct);
            _logger.Info("Ado", $"Interactive ADO sign-in succeeded for {SignedInUser}.");
            return new AdoSignInResult(true, SignedInUser, _expiresOn, null);
        }
        catch (Exception ex)
        {
            _record = null;
            _logger.Error("Ado", "Interactive ADO sign-in failed.", ex);
            return new AdoSignInResult(false, null, null, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _record = null;
            _credential = null;
            _expiresOn = null;
            try
            {
                if (File.Exists(_recordPath))
                    File.Delete(_recordPath);
            }
            catch (Exception ex)
            {
                _logger.Warn("Ado", $"Could not delete cached ADO account record: {ex.Message}");
            }
            _logger.Info("Ado", "Signed out of Azure DevOps (cleared cached account).");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistRecordAsync(CancellationToken ct)
    {
        if (_record is null)
            return;
        await using var fs = File.Create(_recordPath);
        await _record.SerializeAsync(fs, ct);
    }

    public async Task<string> GetAuthHeaderAsync(CancellationToken ct)
    {
        // Lazily pick up a sign-in done in a previous run or the other in-process host (shared record + cache).
        if (_record is null)
            await RestoreAsync(ct);

        try
        {
            var token = await Credential().GetTokenAsync(Request, ct);
            _expiresOn = token.ExpiresOn;
            return $"Bearer {token.Token}";
        }
        catch (AuthenticationRequiredException ex)
        {
            throw new AdoCredentialMissingException(
                "Not signed in to Azure DevOps. Click \u201cSign in\u201d on the Regression tab to authenticate with your AVEVA Microsoft account.",
                ex);
        }
    }

    public string Describe() => IsSignedIn
        ? $"Delegated interactive ({SignedInUser})"
        : "Delegated interactive (not signed in)";
}

/// <summary>Null-object used when interactive auth isn't the configured mode, so the UI can always resolve the service.</summary>
public sealed class UnavailableAdoAuthenticator : IInteractiveAdoAuthenticator
{
    public bool IsAvailable => false;
    public bool IsSignedIn => false;
    public string? SignedInUser => null;
    public DateTimeOffset? TokenExpiresOn => null;

    public Task<AdoSignInResult> SignInAsync(CancellationToken ct) =>
        Task.FromResult(new AdoSignInResult(false, null, null, "Interactive sign-in is not enabled (set Ado:AuthMode to \u201cInteractive\u201d)."));

    public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> RestoreAsync(CancellationToken ct) => Task.FromResult(false);
}
