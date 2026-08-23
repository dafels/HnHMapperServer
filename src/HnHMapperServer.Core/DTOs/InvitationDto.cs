namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for tenant invitation
/// </summary>
public class InvitationDto
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string InviteCode { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? UsedBy { get; set; }
    public DateTime? UsedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool PendingApproval { get; set; }

    /// <summary>Maximum redemptions; null = unlimited.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Redemptions so far.</summary>
    public int UseCount { get; set; }

    /// <summary>Access preset name ("Full" / "Contribute") derived from the stored permissions.</summary>
    public string Preset { get; set; } = "Full";

    /// <summary>Permissions (claim values) a redeemer receives.</summary>
    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// DTO for creating a new invitation
/// </summary>
public class CreateInvitationDto
{
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Options for creating a shareable invite link (request body of POST /api/tenants/{tenantId}/invitations).
/// All fields are optional; the server clamps them.
/// </summary>
public class CreateInvitationRequestDto
{
    /// <summary>7 (default), 30 or 90.</summary>
    public int? ExpiresInDays { get; set; }

    /// <summary>null/0 = unlimited, otherwise 1-100.</summary>
    public int? MaxUses { get; set; }

    /// <summary>"Full" (default) or "Contribute".</summary>
    public string? Preset { get; set; }
}

/// <summary>
/// DTO for validating an invitation. Public (anyone holding the code) - carries only what an invite landing
/// page needs to render, never other codes or user ids.
/// </summary>
public class ValidateInvitationDto
{
    public bool IsValid { get; set; }
    public string? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Username of the admin who created the link.</summary>
    public string? InvitedBy { get; set; }

    /// <summary>Approved members of the tenant.</summary>
    public int MemberCount { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>Access preset the redeemer will receive ("Full" / "Contribute").</summary>
    public string? Preset { get; set; }
}

/// <summary>
/// Result of redeeming an invite link as a signed-in user (POST /api/invitations/{code}/redeem).
/// </summary>
public class RedeemInvitationResultDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    /// <summary>True when the caller was already an approved member; nothing was consumed.</summary>
    public bool AlreadyMember { get; set; }

    public List<string> Permissions { get; set; } = new();
}
