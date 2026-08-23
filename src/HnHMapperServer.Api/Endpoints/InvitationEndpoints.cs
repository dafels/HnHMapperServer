using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Shareable invite links.
///
/// Public surface (anyone holding a code): validate (landing-page preview only) and redeem (signed-in users).
/// Tenant surface (TenantAdmin of THAT tenant, or SuperAdmin): create / list / revoke. The tenant-scoped
/// handlers compare the route tenant with the caller's TenantId claim - the TenantAdmin policy only proves the
/// caller is an admin of their ACTIVE tenant, not of the one in the URL.
/// </summary>
public static class InvitationEndpoints
{
    public static void MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invitations");

        // Public: validate a code (returns only what an invite landing page needs)
        group.MapGet("/validate/{code}", ValidateInvitation)
            .RequireRateLimiting("InviteValidate");

        // Signed-in users: redeem a link -> immediate membership with the link's preset
        group.MapPost("/{code}/redeem", RedeemInvitation)
            .RequireAuthorization()
            .RequireRateLimiting("InviteRedeem")
            .DisableAntiforgery();

        // Tenant-scoped management (TenantAdmin policy + route-vs-claim check in every handler)
        var tenantGroup = app.MapGroup("/api/tenants/{tenantId}/invitations")
            .RequireAuthorization(AuthorizationConstants.Policies.TenantAdmin);

        tenantGroup.MapPost("", CreateInvitation).DisableAntiforgery();
        tenantGroup.MapGet("", GetTenantInvitations);
        tenantGroup.MapDelete("/{invitationId:int}", RevokeInvitation);
    }

    private static async Task<IResult> ValidateInvitation(
        [FromRoute] string code,
        [FromServices] IInvitationService invitationService)
    {
        var result = await invitationService.ValidateInvitationAsync(code);
        return Results.Ok(result);
    }

    private static async Task<IResult> RedeemInvitation(
        [FromRoute] string code,
        HttpContext context,
        [FromServices] UserManager<ApplicationUser> userManager,
        [FromServices] ITenantMembershipService membershipService,
        ILogger<Program> logger)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user == null)
            return Results.Unauthorized();

        var result = await membershipService.RedeemInvitationAsync(code, user.Id);

        if (!result.Succeeded)
        {
            logger.LogInformation("Invite redemption refused for user {UserId}: {Error}", user.Id, result.Error);
            var error = result.Error ?? "Invitation is invalid or expired";
            return error.Contains("not active", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Results.BadRequest(new { error })
                : Results.Conflict(new { error });
        }

        return Results.Ok(new RedeemInvitationResultDto
        {
            TenantId = result.TenantId!,
            TenantName = result.TenantName!,
            AlreadyMember = result.Outcome == RedeemOutcome.AlreadyMember,
            Permissions = result.Permissions.Select(p => p.ToClaimValue()).ToList()
        });
    }

    private static async Task<IResult> CreateInvitation(
        [FromRoute] string tenantId,
        [FromBody] CreateInvitationRequestDto? request,
        HttpContext context,
        [FromServices] ApplicationDbContext db,
        [FromServices] IInvitationService invitationService,
        [FromServices] IAuditService auditService,
        ILogger<Program> logger)
    {
        if (!CallerMayManage(context, tenantId))
            return Results.Forbid();

        var username = context.User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Results.Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == username);
        if (user == null)
            return Results.Unauthorized();

        // SuperAdmins may create links for any tenant; everyone else must be a TenantAdmin of this one (DB truth, not just claims)
        if (!context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            var isAdminHere = await db.TenantUsers
                .IgnoreQueryFilters()
                .AnyAsync(tu => tu.TenantId == tenantId && tu.UserId == user.Id && tu.JoinedAt != default && tu.RoleString == TenantRole.TenantAdmin.ToString());
            if (!isAdminHere)
            {
                logger.LogWarning("User {Username} is not a TenantAdmin of tenant {TenantId}", username, tenantId);
                return Results.Forbid();
            }
        }

        if (request?.Preset != null && !InvitationPresets.IsKnown(request.Preset))
            return Results.BadRequest(new { error = "Unknown access preset" });

        try
        {
            var invitation = await invitationService.CreateInvitationAsync(
                tenantId, username, request?.ExpiresInDays, request?.MaxUses, request?.Preset);

            await auditService.LogAsync(new AuditEntry
            {
                UserId = user.Id,
                TenantId = tenantId,
                Action = "InvitationCreated",
                EntityType = "TenantInvitation",
                EntityId = invitation.Id.ToString(),
                NewValue = $"expires={invitation.ExpiresAt:u}; maxUses={invitation.MaxUses?.ToString() ?? "unlimited"}; preset={invitation.Preset}"
            });

            return Results.Ok(invitation);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Failed to create invitation for tenant {TenantId}", tenantId);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Tenant {TenantId} is not active", tenantId);
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetTenantInvitations(
        [FromRoute] string tenantId,
        HttpContext context,
        [FromServices] IInvitationService invitationService)
    {
        if (!CallerMayManage(context, tenantId))
            return Results.Forbid();

        var invitations = await invitationService.GetTenantInvitationsAsync(tenantId);
        return Results.Ok(invitations);
    }

    private static async Task<IResult> RevokeInvitation(
        [FromRoute] string tenantId,
        [FromRoute] int invitationId,
        HttpContext context,
        [FromServices] IInvitationService invitationService,
        [FromServices] IAuditService auditService,
        [FromServices] UserManager<ApplicationUser> userManager,
        ILogger<Program> logger)
    {
        if (!CallerMayManage(context, tenantId))
            return Results.Forbid();

        try
        {
            await invitationService.RevokeInvitationAsync(invitationId, tenantId);

            var user = await userManager.GetUserAsync(context.User);
            await auditService.LogAsync(new AuditEntry
            {
                UserId = user?.Id,
                TenantId = tenantId,
                Action = "InvitationRevoked",
                EntityType = "TenantInvitation",
                EntityId = invitationId.ToString()
            });

            return Results.Ok(new { message = "Invitation revoked successfully" });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invitation {InvitationId} not found for tenant {TenantId}", invitationId, tenantId);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Cannot revoke invitation {InvitationId}", invitationId);
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The TenantAdmin policy proves the caller administers their ACTIVE tenant; the route may name another one.
    /// SuperAdmins may manage any tenant.
    /// </summary>
    private static bool CallerMayManage(HttpContext context, string tenantId)
    {
        if (context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
            return true;

        var activeTenantId = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantId)?.Value;
        return string.Equals(activeTenantId, tenantId, StringComparison.Ordinal);
    }
}
