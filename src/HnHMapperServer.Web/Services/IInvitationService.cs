using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Web-side client for the invitation API (shareable invite links).
/// </summary>
public interface IInvitationService
{
    /// <param name="expiresInDays">7 (default), 30 or 90.</param>
    /// <param name="maxUses">null = unlimited.</param>
    /// <param name="preset">"Full" (default) or "Contribute".</param>
    Task<InvitationDto?> CreateInvitationAsync(string tenantId, int? expiresInDays = null, int? maxUses = null, string? preset = null);

    Task<List<InvitationDto>> GetInvitationsAsync(string tenantId);

    /// <summary>Public validation + landing-page preview. Never null: invalid codes come back with IsValid=false and a reason.</summary>
    Task<ValidateInvitationDto> ValidateInvitationAsync(string code);

    /// <summary>Redeems a link for the signed-in user. Returns the result, or null with <paramref name="error"/> set.</summary>
    Task<(RedeemInvitationResultDto? Result, string? Error)> RedeemInvitationAsync(string code);

    Task<bool> RevokeInvitationAsync(string tenantId, int invitationId);
}
