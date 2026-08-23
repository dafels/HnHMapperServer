using System.Security.Claims;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Extensions;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HnHMapperServer.Infrastructure.Identity;

/// <summary>
/// Adds the active tenant's claims (TenantId, TenantRole, Role, TenantPermission x N) to the user principal.
/// Registered by BOTH the Web and the API process - there is exactly one implementation so the two can never
/// drift. Identity calls it on sign-in and on every cookie revalidation; the active tenant comes from
/// <see cref="ActiveTenantMembershipResolver"/> (persisted ActiveTenantId with a deterministic fallback).
/// A user with no approved membership simply gets no tenant claims - that is a normal state now (they are
/// routed to the create-or-join screen), not an error.
/// </summary>
public class TenantClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TenantClaimsPrincipalFactory> _logger;

    public TenantClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor,
        ApplicationDbContext db,
        ILogger<TenantClaimsPrincipalFactory> logger)
        : base(userManager, roleManager, optionsAccessor)
    {
        _db = db;
        _logger = logger;
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        var resolution = await ActiveTenantMembershipResolver.ResolveAsync(_db, user.Id, user.ActiveTenantId);
        var membership = resolution.Membership;

        if (membership == null)
        {
            _logger.LogDebug("No approved tenant membership for user {UserId}; issuing principal without tenant claims", user.Id);
            return identity;
        }

        identity.AddClaim(new Claim(AuthorizationConstants.ClaimTypes.TenantId, membership.TenantId));
        identity.AddClaim(new Claim(AuthorizationConstants.ClaimTypes.TenantRole, membership.Role.ToClaimValue()));

        // Standard Role claim so [Authorize(Roles = ...)] and IsInRole() work for the active tenant's role
        identity.AddClaim(new Claim(ClaimTypes.Role, membership.Role.ToClaimValue()));

        foreach (var permission in resolution.Permissions)
        {
            identity.AddClaim(new Claim(AuthorizationConstants.ClaimTypes.TenantPermission, permission.ToClaimValue()));
        }

        _logger.LogDebug("Tenant claims for user {UserId}: TenantId={TenantId}, Role={Role}, Permissions={PermCount}",
            user.Id, membership.TenantId, membership.Role.ToClaimValue(), resolution.Permissions.Count);

        return identity;
    }
}
