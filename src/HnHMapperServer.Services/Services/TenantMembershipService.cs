using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Creates/approves tenant memberships and redeems invite links. Every query is explicit about the tenant
/// (IgnoreQueryFilters + predicates) because callers run without tenant context: a brand-new account joining
/// its first tenant, an anonymous registration, or the Web process handling an external sign-in callback.
/// Never writes <c>JoinedAt = default</c>: the pending sentinel is only ever produced by legacy data now.
/// </summary>
public class TenantMembershipService : ITenantMembershipService
{
    private readonly ApplicationDbContext _db;
    private readonly IInvitationService _invitations;
    private readonly IAuditService _audit;
    private readonly ILogger<TenantMembershipService> _logger;

    public TenantMembershipService(
        ApplicationDbContext db,
        IInvitationService invitations,
        IAuditService audit,
        ILogger<TenantMembershipService> logger)
    {
        _db = db;
        _invitations = invitations;
        _audit = audit;
        _logger = logger;
    }

    public async Task<MembershipResult> AddMemberAsync(AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);

        if (tenant == null)
            return new MembershipResult(MembershipOutcome.TenantNotFound, request.TenantId, request.TenantId, Array.Empty<Permission>());
        if (!tenant.IsActive)
            return new MembershipResult(MembershipOutcome.TenantInactive, tenant.Id, tenant.Name, Array.Empty<Permission>());

        var existing = await _db.TenantUsers
            .IgnoreQueryFilters()
            .Include(tu => tu.Permissions)
            .FirstOrDefaultAsync(tu => tu.TenantId == request.TenantId && tu.UserId == request.UserId, cancellationToken);

        MembershipOutcome outcome;
        TenantUserEntity membership;
        var now = DateTime.UtcNow;

        if (existing != null && existing.JoinedAt != default)
        {
            // Already in: nothing to grant. Honour the active-tenant request (double-clicked links, re-joins).
            if (request.SetActiveTenant)
                await SetActiveTenantAsync(request.UserId, tenant.Id, cancellationToken);

            return new MembershipResult(
                MembershipOutcome.AlreadyMember, tenant.Id, tenant.Name,
                existing.Permissions.Select(p => p.Permission).ToList());
        }

        if (existing != null)
        {
            // Legacy pending registration: approve in place (the canonical "flip" the old approval endpoint did).
            existing.JoinedAt = now;
            existing.PendingApproval = false;
            existing.JoinSource = request.JoinSource;
            existing.InvitationId ??= request.InvitationId;
            if (request.Role == TenantRole.TenantAdmin)
                existing.Role = TenantRole.TenantAdmin;

            var have = existing.Permissions.Select(p => p.Permission).ToHashSet();
            foreach (var permission in request.Permissions.Where(p => !have.Contains(p)))
            {
                existing.Permissions.Add(new TenantPermissionEntity { TenantUserId = existing.Id, Permission = permission });
            }

            // Keep the legacy 7-day purge from deleting a user who has now been approved
            var legacyInvitation = await _db.TenantInvitations
                .FirstOrDefaultAsync(i => i.UsedBy == request.UserId && i.TenantId == request.TenantId && i.PendingApproval, cancellationToken);
            if (legacyInvitation != null)
                legacyInvitation.PendingApproval = false;

            membership = existing;
            outcome = MembershipOutcome.ApprovedExisting;
        }
        else
        {
            membership = new TenantUserEntity
            {
                TenantId = request.TenantId,
                UserId = request.UserId,
                Role = request.Role,
                JoinedAt = now,
                PendingApproval = false,
                JoinSource = request.JoinSource,
                InvitationId = request.InvitationId
            };
            foreach (var permission in request.Permissions.Distinct())
            {
                membership.Permissions.Add(new TenantPermissionEntity { Permission = permission });
            }
            _db.TenantUsers.Add(membership);
            outcome = MembershipOutcome.Joined;
        }

        if (request.SetActiveTenant)
            await SetActiveTenantAsync(request.UserId, tenant.Id, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        var grantedPermissions = membership.Permissions.Select(p => p.Permission).ToList();

        await _audit.LogAsync(new AuditEntry
        {
            UserId = request.PerformedByUserId,
            TenantId = tenant.Id,
            Action = request.AuditAction,
            EntityType = "TenantUser",
            EntityId = request.UserId,
            NewValue = $"outcome={outcome}; role={membership.Role.ToClaimValue()}; " +
                       $"permissions={string.Join(",", grantedPermissions.Select(p => p.ToClaimValue()))}; " +
                       $"source={request.JoinSource}" +
                       (request.InvitationId.HasValue ? $"; invitation={request.InvitationId}" : string.Empty)
        });

        _logger.LogInformation("Membership {Outcome}: user {UserId} in tenant {TenantId} as {Role} via {Source}",
            outcome, request.UserId, tenant.Id, membership.Role, request.JoinSource);

        return new MembershipResult(outcome, tenant.Id, tenant.Name, grantedPermissions);
    }

    public async Task<RedeemInvitationResult> RedeemInvitationAsync(string inviteCode, string userId, CancellationToken cancellationToken = default)
    {
        var validation = await _invitations.ValidateInvitationAsync(inviteCode);
        if (!validation.IsValid || validation.TenantId == null)
            return Invalid(validation.ErrorMessage ?? "Invitation is invalid or expired");

        // Already an approved member: idempotent success, the link's use is NOT consumed.
        var alreadyMember = await _db.TenantUsers
            .IgnoreQueryFilters()
            .Include(tu => tu.Permissions)
            .FirstOrDefaultAsync(tu => tu.TenantId == validation.TenantId && tu.UserId == userId && tu.JoinedAt != default, cancellationToken);
        if (alreadyMember != null)
        {
            await SetActiveTenantAsync(userId, validation.TenantId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return new RedeemInvitationResult(RedeemOutcome.AlreadyMember, validation.TenantId, validation.TenantName,
                alreadyMember.Permissions.Select(p => p.Permission).ToList(), null);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await _invitations.TryClaimUseAsync(inviteCode, userId, cancellationToken);
        if (claimed == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            // Lost the race (or revoked/expired since validation): re-validate for the precise reason.
            var again = await _invitations.ValidateInvitationAsync(inviteCode);
            return Invalid(again.IsValid ? "Invitation could not be redeemed, please try again" : again.ErrorMessage ?? "Invitation is invalid or expired");
        }

        var permissions = claimed.Permissions.Count == 0
            ? InvitationPresets.FullPermissions
            : claimed.Permissions.Select(p => p.ToPermission()).Distinct().ToList();

        var membership = await AddMemberAsync(new AddMemberRequest
        {
            TenantId = claimed.TenantId,
            UserId = userId,
            Role = TenantRole.TenantUser,          // never client-supplied
            Permissions = permissions,
            PerformedByUserId = userId,
            AuditAction = "InvitationRedeemed",
            JoinSource = MembershipJoinSources.InviteLink,
            InvitationId = claimed.Id,
            SetActiveTenant = true
        }, cancellationToken);

        if (!membership.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Invalid(membership.Outcome == MembershipOutcome.TenantInactive ? "Tenant is not active" : "Tenant not found");
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Invite {InvitationId} redeemed by user {UserId} for tenant {TenantId} (use {UseCount}/{MaxUses})",
            claimed.Id, userId, claimed.TenantId, claimed.UseCount, claimed.MaxUses?.ToString() ?? "∞");

        return new RedeemInvitationResult(RedeemOutcome.Joined, membership.TenantId, membership.TenantName, membership.Permissions, null);
    }

    private async Task SetActiveTenantAsync(string userId, string tenantId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user != null && !string.Equals(user.ActiveTenantId, tenantId, StringComparison.Ordinal))
            user.ActiveTenantId = tenantId;
    }

    private static RedeemInvitationResult Invalid(string error) =>
        new(RedeemOutcome.Invalid, null, null, Array.Empty<Permission>(), error);
}
