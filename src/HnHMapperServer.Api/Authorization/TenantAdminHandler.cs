using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Extensions;
using Microsoft.AspNetCore.Authorization;

namespace HnHMapperServer.Api.Authorization;

/// <summary>
/// Authorization handler for TenantAdminRequirement.
/// Succeeds if:
/// 1. User has SuperAdmin role (global bypass), OR
/// 2. User has TenantAdmin role in the current tenant
///
/// INVARIANT (multi-membership): the TenantRole claim describes the user's ACTIVE tenant only. This policy
/// therefore proves "admin of the active tenant", not "admin of the tenant named in the route". Every
/// /api/tenants/{tenantId}/... handler must additionally compare the route tenantId with the TenantId claim
/// (SuperAdmins excepted) - see TenantAdminEndpoints.GetTenantUsers for the canonical check.
/// </summary>
public class TenantAdminHandler : AuthorizationHandler<TenantAdminRequirement>
{
    private readonly ILogger<TenantAdminHandler> _logger;

    public TenantAdminHandler(ILogger<TenantAdminHandler> logger)
    {
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAdminRequirement requirement)
    {
        // SuperAdmin bypasses tenant checks
        if (context.User.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Check tenant role (using enum comparison)
        var tenantRoleClaim = context.User.FindFirst(AuthorizationConstants.ClaimTypes.TenantRole)?.Value;

        if (!string.IsNullOrEmpty(tenantRoleClaim) &&
            tenantRoleClaim.TryToTenantRole(out var tenantRole) &&
            tenantRole == TenantRole.TenantAdmin)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
