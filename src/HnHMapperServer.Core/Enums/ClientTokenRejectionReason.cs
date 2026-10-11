namespace HnHMapperServer.Core.Enums;

/// <summary>
/// Why the tenant middleware answered a /client request 401. The 401 status and JSON bodies are
/// the same for every reason (game clients depend on them); the reason only drives logging and the
/// <c>hnhmapper.client_token.rejection</c> tag on http.server.request.duration.
/// </summary>
public enum ClientTokenRejectionReason
{
    /// <summary>The path segment has no <c>{tenantId}_</c> prefix.</summary>
    MalformedToken,
    /// <summary>Nothing (or only whitespace) after the underscore.</summary>
    EmptySecret,
    /// <summary>No token row with this hash for this tenant (revoked, member removed, or never existed).</summary>
    NotFound,
    /// <summary>The row's ExpiresAt has passed (nothing writes ExpiresAt today; kept for parity).</summary>
    Expired,
    /// <summary>The token exists but its tenant is suspended (or gone).</summary>
    TenantInactive
}
