namespace HnHMapperServer.Services.Interfaces;

public enum TenantProvisionOutcome
{
    Created,
    /// <summary>TenantSelfService:Enabled is false.</summary>
    Disabled,
    /// <summary>The user already administers TenantSelfService:MaxOwnedTenants tenants.</summary>
    CapReached,
    /// <summary>The requested display name failed validation.</summary>
    InvalidName,
    /// <summary>Map creation is reserved for accounts with a verified Steam/Discord identity; this account has none.</summary>
    NotEligible,
    UserNotFound
}

/// <summary>What the current user may do (drives the "Create a new map" card).</summary>
public sealed record TenantProvisionOptions(bool Enabled, bool Eligible, string? Reason);

public sealed record TenantProvisionResult(
    TenantProvisionOutcome Outcome,
    string? TenantId,
    string? TenantName,
    string? Error)
{
    public bool Succeeded => Outcome == TenantProvisionOutcome.Created;
}

/// <summary>
/// Self-service tenant creation ("Create a new map"): any signed-in player can create a tenant and becomes its
/// TenantAdmin. Quota, caps and the kill switch are server-side configuration - the request carries only an
/// optional display name.
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>Whether players may create maps right now (SuperAdmin → Sign-in &amp; onboarding).</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Feature switch + per-user eligibility (external identity rule) with a player-facing reason.</summary>
    Task<TenantProvisionOptions> GetOptionsAsync(string userId, CancellationToken cancellationToken = default);

    Task<TenantProvisionResult> CreateOwnedTenantAsync(string ownerUserId, string? displayName, CancellationToken cancellationToken = default);
}
