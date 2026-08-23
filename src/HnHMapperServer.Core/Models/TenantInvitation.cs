namespace HnHMapperServer.Core.Models;

/// <summary>
/// Manages invitation codes and pending registrations
/// </summary>
public sealed class TenantInvitationEntity
{
    /// <summary>
    /// Auto-increment primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to Tenants
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// GUID-based unique invite code
    /// </summary>
    public string InviteCode { get; set; } = string.Empty;

    /// <summary>
    /// Username of creator
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// ISO 8601 UTC timestamp when invitation was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// ISO 8601 UTC timestamp when invitation expires (7 days from creation)
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Username who used this invite
    /// </summary>
    /// <summary>
    /// Identity user ID of the LAST redeemer (multi-use links are redeemed by many users; every redemption is
    /// also an "InvitationRedeemed" audit row). Despite the historical name this has always held the user id.
    /// </summary>
    public string? UsedBy { get; set; }

    /// <summary>
    /// ISO 8601 UTC timestamp when invite was used
    /// </summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// Status: 'Active', 'Used', 'Expired', or 'Revoked'
    /// </summary>
    public string Status { get; set; } = "Active";

    /// <summary>
    /// Whether registration is pending approval (1) or not (0)
    /// </summary>
    public bool PendingApproval { get; set; } = false;

    /// <summary>Maximum number of redemptions; null = unlimited. Pre-existing links were back-filled to 1.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Number of successful redemptions so far (incremented atomically).</summary>
    public int UseCount { get; set; }

    /// <summary>
    /// Permissions granted to redeemers, stored as claim values (JSON list). Empty = all five (legacy links).
    /// The role is always TenantUser.
    /// </summary>
    public List<string> Permissions { get; set; } = new();
}
