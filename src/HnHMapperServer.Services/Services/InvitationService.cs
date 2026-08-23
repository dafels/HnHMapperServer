using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public class InvitationService : IInvitationService
{
    private const int DefaultExpiryDays = 7;
    private const int MaxUsesCeiling = 100;

    private readonly ITenantInvitationRepository _invitationRepository;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<InvitationService> _logger;

    public InvitationService(
        ITenantInvitationRepository invitationRepository,
        ApplicationDbContext context,
        ILogger<InvitationService> logger)
    {
        _invitationRepository = invitationRepository;
        _context = context;
        _logger = logger;
    }

    public async Task<InvitationDto> CreateInvitationAsync(string tenantId, string createdBy, int? expiresInDays = null, int? maxUses = null, string? preset = null)
    {
        // Verify tenant exists and is active
        var tenant = await _context.Tenants.FindAsync(tenantId);
        if (tenant == null)
        {
            throw new ArgumentException($"Tenant {tenantId} not found");
        }

        if (!tenant.IsActive)
        {
            throw new InvalidOperationException($"Tenant {tenantId} is not active");
        }

        // Server clamps every option: the client cannot mint a never-expiring or unlimited link by accident
        var days = expiresInDays.HasValue && IInvitationService.AllowedExpiryDays.Contains(expiresInDays.Value)
            ? expiresInDays.Value
            : DefaultExpiryDays;
        int? uses = maxUses is null or <= 0 ? null : Math.Min(maxUses.Value, MaxUsesCeiling);
        var permissions = InvitationPresets.Expand(preset).Select(p => p.ToClaimValue()).ToList();

        var invitation = new TenantInvitationEntity
        {
            TenantId = tenantId,
            InviteCode = Guid.NewGuid().ToString(),   // 122 random bits - the code IS the credential
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(days),
            Status = "Active",
            PendingApproval = false,
            MaxUses = uses,
            UseCount = 0,
            Permissions = permissions
        };

        var created = await _invitationRepository.CreateAsync(invitation);

        _logger.LogInformation("Created invitation {InvitationId} for tenant {TenantId} by {CreatedBy} (expires {ExpiresAt:u}, maxUses={MaxUses}, preset={Preset})",
            created.Id, tenantId, createdBy, created.ExpiresAt, uses?.ToString() ?? "unlimited", InvitationPresets.NameFor(InvitationPresets.Expand(preset)));

        return MapToDto(created, tenant.Name);
    }

    public async Task<InvitationDto?> GetInvitationAsync(string inviteCode)
    {
        var invitation = await _invitationRepository.GetByInviteCodeAsync(inviteCode);
        if (invitation == null)
        {
            return null;
        }

        var tenant = await _context.Tenants.FindAsync(invitation.TenantId);
        return MapToDto(invitation, tenant?.Name ?? invitation.TenantId);
    }

    public async Task<ValidateInvitationDto> ValidateInvitationAsync(string inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode))
            return Invalid("Invitation is invalid or expired");

        var invitation = await _invitationRepository.GetByInviteCodeAsync(inviteCode.Trim());

        // Unknown codes get the same generic answer as dead ones - no existence oracle for guessers.
        if (invitation == null)
            return Invalid("Invitation is invalid or expired");

        if (invitation.Status == "Revoked")
            return Invalid("Invitation has been revoked");

        if (invitation.Status == "Used" || (invitation.MaxUses.HasValue && invitation.UseCount >= invitation.MaxUses.Value))
            return Invalid("Invitation has no remaining uses");

        if (invitation.Status != "Active")
            return Invalid($"Invitation is {invitation.Status.ToLowerInvariant()}");

        if (invitation.ExpiresAt < DateTime.UtcNow)
            return Invalid("Invitation has expired");

        var tenant = await _context.Tenants.FindAsync(invitation.TenantId);
        if (tenant == null || !tenant.IsActive)
            return Invalid("This map is no longer active");

        var memberCount = await _context.TenantUsers
            .IgnoreQueryFilters()
            .CountAsync(tu => tu.TenantId == tenant.Id && tu.JoinedAt != default);

        return new ValidateInvitationDto
        {
            IsValid = true,
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            InvitedBy = invitation.CreatedBy,
            MemberCount = memberCount,
            ExpiresAt = invitation.ExpiresAt,
            Preset = PresetOf(invitation)
        };
    }

    public async Task<List<InvitationDto>> GetTenantInvitationsAsync(string tenantId)
    {
        var invitations = await _invitationRepository.GetByTenantIdAsync(tenantId);
        var tenant = await _context.Tenants.FindAsync(tenantId);
        var tenantName = tenant?.Name ?? tenantId;

        return invitations.Select(i => MapToDto(i, tenantName)).ToList();
    }

    public async Task RevokeInvitationAsync(int invitationId, string tenantId)
    {
        var invitation = await _invitationRepository.GetByIdAsync(invitationId);
        if (invitation == null || !string.Equals(invitation.TenantId, tenantId, StringComparison.Ordinal))
        {
            // Same answer for "not yours" and "does not exist": never confirm another tenant's invitation ids
            throw new ArgumentException($"Invitation {invitationId} not found");
        }

        if (invitation.Status == "Revoked")
        {
            return; // idempotent
        }

        if (invitation.Status == "Used" && invitation.MaxUses.HasValue && invitation.UseCount >= invitation.MaxUses.Value)
        {
            throw new InvalidOperationException("Invitation has already been fully used");
        }

        invitation.Status = "Revoked";
        await _invitationRepository.UpdateAsync(invitation);

        _logger.LogInformation("Revoked invitation {InvitationId} for tenant {TenantId}",
            invitationId, invitation.TenantId);
    }

    public async Task<TenantInvitationEntity?> TryClaimUseAsync(string inviteCode, string redeemerUserId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Single conditional UPDATE: two redeemers racing for the last use can never both win.
        var affected = await _context.TenantInvitations
            .Where(i => i.InviteCode == inviteCode
                        && i.Status == "Active"
                        && i.ExpiresAt > now
                        && (i.MaxUses == null || i.UseCount < i.MaxUses))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.UseCount, i => i.UseCount + 1)
                .SetProperty(i => i.UsedAt, now)
                .SetProperty(i => i.UsedBy, redeemerUserId), cancellationToken);

        if (affected == 0)
            return null;

        // ExecuteUpdate bypasses the change tracker: reload whatever copy the tracker holds
        var invitation = await _context.TenantInvitations.FirstAsync(i => i.InviteCode == inviteCode, cancellationToken);
        await _context.Entry(invitation).ReloadAsync(cancellationToken);

        if (invitation.MaxUses.HasValue && invitation.UseCount >= invitation.MaxUses.Value && invitation.Status == "Active")
        {
            invitation.Status = "Used";
            await _context.SaveChangesAsync(cancellationToken);
        }

        return invitation;
    }

    private static ValidateInvitationDto Invalid(string message) =>
        new() { IsValid = false, ErrorMessage = message };

    private static string PresetOf(TenantInvitationEntity entity) =>
        entity.Permissions.Count == 0
            ? InvitationPresets.Full
            : InvitationPresets.NameFor(entity.Permissions.Select(p => p.ToPermission()).ToList());

    private static InvitationDto MapToDto(TenantInvitationEntity entity, string tenantName)
    {
        var permissions = entity.Permissions.Count == 0
            ? InvitationPresets.FullPermissions.Select(p => p.ToClaimValue()).ToList()
            : entity.Permissions.ToList();

        return new InvitationDto
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            TenantName = tenantName,
            InviteCode = entity.InviteCode,
            CreatedBy = entity.CreatedBy,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt,
            UsedBy = entity.UsedBy,
            UsedAt = entity.UsedAt,
            Status = entity.Status,
            PendingApproval = entity.PendingApproval,
            MaxUses = entity.MaxUses,
            UseCount = entity.UseCount,
            Preset = PresetOf(entity),
            Permissions = permissions
        };
    }
}
