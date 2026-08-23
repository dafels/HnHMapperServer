using HnHMapperServer.Core.Enums;

namespace HnHMapperServer.Services.Interfaces;

public enum MembershipOutcome
{
    /// <summary>A new approved membership row was created.</summary>
    Joined,
    /// <summary>A legacy pending row (JoinedAt == default) was approved in place.</summary>
    ApprovedExisting,
    /// <summary>The user already had an approved membership; nothing changed (except the active tenant, if requested).</summary>
    AlreadyMember,
    TenantNotFound,
    TenantInactive
}

public sealed record MembershipResult(
    MembershipOutcome Outcome,
    string TenantId,
    string TenantName,
    IReadOnlyList<Permission> Permissions)
{
    public bool Succeeded => Outcome is MembershipOutcome.Joined or MembershipOutcome.ApprovedExisting or MembershipOutcome.AlreadyMember;
}

/// <summary>
/// Everything needed to add a user to a tenant. Role and permissions are decided by the CALLER's policy
/// (invite preset, self-create = admin, superadmin assignment) - never by client input reaching this type directly.
/// </summary>
public sealed class AddMemberRequest
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public TenantRole Role { get; init; } = TenantRole.TenantUser;
    public IReadOnlyCollection<Permission> Permissions { get; init; } = Array.Empty<Permission>();

    /// <summary>Identity user id of the actor for the audit row (the user themselves for self-service flows).</summary>
    public required string PerformedByUserId { get; init; }

    /// <summary>Audit action name, e.g. "InvitationRedeemed", "TenantSelfCreated", "UserApproved".</summary>
    public required string AuditAction { get; init; }

    /// <summary>See <see cref="HnHMapperServer.Core.Constants.MembershipJoinSources"/>.</summary>
    public required string JoinSource { get; init; }

    public int? InvitationId { get; init; }

    /// <summary>Also make this tenant the user's active tenant (claims follow on the next cookie issue/revalidation).</summary>
    public bool SetActiveTenant { get; init; }
}

public enum RedeemOutcome
{
    Joined,
    AlreadyMember,
    /// <summary>Unknown, expired, revoked, exhausted, or the tenant is not active - see Error.</summary>
    Invalid
}

public sealed record RedeemInvitationResult(
    RedeemOutcome Outcome,
    string? TenantId,
    string? TenantName,
    IReadOnlyList<Permission> Permissions,
    string? Error)
{
    public bool Succeeded => Outcome is RedeemOutcome.Joined or RedeemOutcome.AlreadyMember;
}

/// <summary>
/// The one place membership rows are created/approved. Replaces the four historical inline copies
/// (register-with-invite, superadmin assignment, bootstrap, tenant-admin approval).
/// </summary>
public interface ITenantMembershipService
{
    Task<MembershipResult> AddMemberAsync(AddMemberRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a shareable invite link for a signed-in user: validates, atomically claims a use, creates the
    /// approved membership with the link's permissions, and makes the tenant active. A user who is already an
    /// approved member gets <see cref="RedeemOutcome.AlreadyMember"/> and no use is consumed. All-or-nothing.
    /// </summary>
    Task<RedeemInvitationResult> RedeemInvitationAsync(string inviteCode, string userId, CancellationToken cancellationToken = default);
}
