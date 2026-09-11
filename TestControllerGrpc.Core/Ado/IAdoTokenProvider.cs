namespace TestControllerGrpc.Ado;

/// <summary>Provides bearer tokens/auth headers for Azure DevOps REST calls.</summary>
public interface IAdoTokenProvider
{
    /// <summary>Returns a ready-to-use "Authorization" header value (e.g. "Bearer xyz" or "Basic xyz").</summary>
    Task<string> GetAuthHeaderAsync(CancellationToken ct);

    /// <summary>Human-readable description of the credential source, for logging/diagnostics — never the secret itself.</summary>
    string Describe();

    /// <summary>
    /// Remediation text for an ADO 401/403, specific to this credential type. 401 and 403 have different causes
    /// and different fixes, so they must never share one message.
    /// </summary>
    string AuthFailureHint(int statusCode) => statusCode == 401
        ? "The credential was rejected by Azure DevOps - it is expired, revoked, or not a member of the organisation."
        : "The credential authenticated but is not authorised - check project permissions, or an Entra Conditional " +
          "Access policy blocking this credential type.";
}

/// <summary>Thrown when the configured credential (PAT, cert, secret) cannot be located.</summary>
public sealed class AdoCredentialMissingException : Exception
{
    public AdoCredentialMissingException(string message) : base(message) { }
    public AdoCredentialMissingException(string message, Exception inner) : base(message, inner) { }
}
