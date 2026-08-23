using HnHMapperServer.Core.Constants;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace HnHMapperServer.Web.Services;

/// <summary>
/// Revalidating authentication state provider that checks security stamps
/// to ensure users are logged out when their roles/permissions change.
/// It also invalidates a circuit whose principal no longer reflects the user's persisted active tenant
/// (switched on another tab/device, membership removed, tenant suspended): returning <c>false</c> sends the
/// circuit through RedirectToLogin, where the browser's (already refreshed) cookie signs it straight back in
/// with the correct tenant claims.
/// </summary>
public class RevalidatingIdentityAuthenticationStateProvider<TUser>
    : RevalidatingServerAuthenticationStateProvider where TUser : class
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentityOptions _options;

    public RevalidatingIdentityAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> optionsAccessor)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = optionsAccessor.Value;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // Get services from a new scope to ensure fresh data each cycle
        await using var scope = _scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TUser>>();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<TUser>>();

        var principal = authenticationState.User;

        // If no user is authenticated, state is invalid
        var user = await userManager.GetUserAsync(principal);
        if (user == null)
        {
            return false;
        }

        // Active tenant drift: the circuit's claims say one tenant, the persisted selection resolves to another.
        if (user is ApplicationUser appUser)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var expectedTenantId = await ActiveTenantMembershipResolver.ResolveTenantIdAsync(
                db, appUser.Id, appUser.ActiveTenantId, cancellationToken);
            var circuitTenantId = principal.FindFirstValue(AuthorizationConstants.ClaimTypes.TenantId);
            if (!string.Equals(expectedTenantId, circuitTenantId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // If security stamp isn't supported, accept as valid
        if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }

        // If the principal does not carry a security stamp claim, do NOT eject the user.
        // This avoids false logouts for sessions created before we added the stamp claim.
        var principalStamp = principal.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
        if (string.IsNullOrEmpty(principalStamp))
        {
            return true;
        }

        // Prefer Identity's built-in validation
        var validated = await signInManager.ValidateSecurityStampAsync(principal);
        if (validated != null)
        {
            return true;
        }

        // Security stamp changed. Attempt silent refresh to update claims without forcing logout.
        try
        {
            await signInManager.RefreshSignInAsync(user);
            return true;
        }
        catch
        {
            // If refresh fails, signal invalid state so the framework logs the user out
            return false;
        }
    }
}
