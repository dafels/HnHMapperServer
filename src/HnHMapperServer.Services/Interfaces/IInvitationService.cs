using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Shareable invite links. A link is a bearer credential: whoever holds it joins the tenant immediately with
/// the link's permission preset (role always TenantUser). Links are multi-use (MaxUses null = unlimited) and expire.
/// </summary>
public interface IInvitationService
{
    /// <summary>Allowed expiry choices in days; anything else is clamped to the default (7).</summary>
    static readonly IReadOnlyList<int> AllowedExpiryDays = new[] { 7, 30, 90 };

    /// <param name="expiresInDays">7 (default), 30 or 90.</param>
    /// <param name="maxUses">null/0 = unlimited, otherwise clamped to 1-100.</param>
    /// <param name="preset">"Full" (default) or "Contribute" - see InvitationPresets.</param>
    Task<InvitationDto> CreateInvitationAsync(string tenantId, string createdBy, int? expiresInDays = null, int? maxUses = null, string? preset = null);

    Task<InvitationDto?> GetInvitationAsync(string inviteCode);

    /// <summary>Public-safe validation + landing-page preview (tenant name, inviter, member count, expiry).</summary>
    Task<ValidateInvitationDto> ValidateInvitationAsync(string inviteCode);

    Task<List<InvitationDto>> GetTenantInvitationsAsync(string tenantId);

    /// <summary>Revokes a link; the invitation must belong to <paramref name="tenantId"/> (ownership check).</summary>
    Task RevokeInvitationAsync(int invitationId, string tenantId);

    /// <summary>
    /// Atomically claims one use of the link (conditional UPDATE: active, not expired, uses remaining).
    /// Returns the updated invitation, or null when the claim lost - the caller re-validates for the reason.
    /// Flips Status to "Used" when the final use is consumed.
    /// </summary>
    Task<TenantInvitationEntity?> TryClaimUseAsync(string inviteCode, string redeemerUserId, CancellationToken cancellationToken = default);
}
