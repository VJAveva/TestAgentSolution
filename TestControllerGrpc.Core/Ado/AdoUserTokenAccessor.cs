using System.Security.Cryptography;
using System.Text;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Carries a per-request delegated Azure DevOps token (the signed-in web user's own Entra token).
/// AsyncLocal rather than IHttpContextAccessor so Core stays free of an ASP.NET dependency and the
/// value still flows through the async call chain into the singleton ADO query stack.
/// </summary>
public interface IAdoUserTokenAccessor
{
    string? Token { get; set; }

    /// <summary>
    /// Stable, non-secret identifier for whichever credential is in play. Cache keys MUST include this:
    /// per-user tokens see different ADO data, so a shared cache keyed without it leaks across users.
    /// </summary>
    string CredentialFingerprint { get; }
}

public sealed class AsyncLocalAdoUserTokenAccessor : IAdoUserTokenAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? Token
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public string CredentialFingerprint =>
        Token is { Length: > 0 } token ? Fingerprint(token) : "host";

    private static string Fingerprint(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash, 0, 6);
    }
}

/// <summary>
/// Uses the signed-in web user's delegated token when the current request carries one, otherwise falls back
/// to the host's configured credential (PAT / service principal). Registered as a singleton wrapper so the
/// existing singleton ADO graph needs no lifetime changes, and background work with no request context
/// (index maintenance) transparently keeps using the host credential.
/// </summary>
public sealed class AmbientAdoTokenProvider : IAdoTokenProvider
{
    private readonly IAdoUserTokenAccessor _accessor;
    private readonly IAdoTokenProvider _fallback;

    public AmbientAdoTokenProvider(IAdoUserTokenAccessor accessor, IAdoTokenProvider fallback)
    {
        _accessor = accessor;
        _fallback = fallback;
    }

    public Task<string> GetAuthHeaderAsync(CancellationToken ct) =>
        _accessor.Token is { Length: > 0 } token
            ? Task.FromResult($"Bearer {token}")
            : _fallback.GetAuthHeaderAsync(ct);

    public string Describe() =>
        _accessor.Token is { Length: > 0 }
            ? "Delegated web user (Entra)"
            : _fallback.Describe();
}
