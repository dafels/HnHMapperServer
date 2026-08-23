using System.Text.RegularExpressions;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Creates a tenant for a player and makes them its admin, in one transaction. Mirrors the bootstrap
/// "complete tenant + admin" shape (Api/Program.cs) but with server-decided quota and a per-user cap.
/// </summary>
public partial class TenantProvisioningService : ITenantProvisioningService
{
    public const int DefaultQuotaMB = 1024;
    public const int DefaultMaxOwned = 3;
    public const int MinNameLength = 3;
    public const int MaxNameLength = 40;

    // Letters (any script), digits, spaces and a few punctuation marks villages actually use
    [GeneratedRegex(@"^[\p{L}\p{N} '\-._]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private readonly ApplicationDbContext _db;
    private readonly ITenantService _tenantService;
    private readonly ITenantMembershipService _membership;
    private readonly ITenantFilePathService _filePaths;
    private readonly IAuthSettingsStore _settings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        ApplicationDbContext db,
        ITenantService tenantService,
        ITenantMembershipService membership,
        ITenantFilePathService filePaths,
        IAuthSettingsStore settings,
        IConfiguration configuration,
        ILogger<TenantProvisioningService> logger)
    {
        _db = db;
        _tenantService = tenantService;
        _membership = membership;
        _filePaths = filePaths;
        _settings = settings;
        _configuration = configuration;
        _logger = logger;
    }

    public const string NotEligibleMessage =
        "Creating a map needs a verified Steam or Discord identity. Sign in with Steam or Discord, link one to this account under Account, "
        + "join a map with an invite link, or ask a superadmin to create the map for you.";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        (await _settings.GetPolicyAsync(cancellationToken)).SelfServiceTenantsEnabled;

    public async Task<TenantProvisionOptions> GetOptionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var policy = await _settings.GetPolicyAsync(cancellationToken);
        if (!policy.SelfServiceTenantsEnabled)
            return new TenantProvisionOptions(false, false, "Creating new maps is currently disabled on this server.");

        var eligible = !policy.SelfServiceTenantsRequireExternalIdentity || await HasVerifiedIdentityAsync(userId, cancellationToken);
        return new TenantProvisionOptions(true, eligible, eligible ? null : NotEligibleMessage);
    }

    /// <summary>
    /// Password accounts are self-asserted; a Steam/Discord login (at sign-up or linked later) is an identity a
    /// provider vouched for. Superadmins are always eligible.
    /// </summary>
    private async Task<bool> HasVerifiedIdentityAsync(string userId, CancellationToken cancellationToken)
    {
        if (await _db.UserLogins.AnyAsync(l => l.UserId == userId, cancellationToken))
            return true;

        return await _db.UserRoles
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .AnyAsync(x => x.UserId == userId && x.Name == AuthorizationConstants.Roles.SuperAdmin, cancellationToken);
    }

    public async Task<TenantProvisionResult> CreateOwnedTenantAsync(string ownerUserId, string? displayName, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetPolicyAsync(cancellationToken);
        if (!settings.SelfServiceTenantsEnabled)
            return new TenantProvisionResult(TenantProvisionOutcome.Disabled, null, null, "Creating new maps is currently disabled on this server.");

        if (settings.SelfServiceTenantsRequireExternalIdentity && !await HasVerifiedIdentityAsync(ownerUserId, cancellationToken))
            return new TenantProvisionResult(TenantProvisionOutcome.NotEligible, null, null, NotEligibleMessage);

        var name = NormalizeName(displayName);
        if (name == null && !string.IsNullOrWhiteSpace(displayName))
            return new TenantProvisionResult(TenantProvisionOutcome.InvalidName, null, null,
                $"Map name must be {MinNameLength}-{MaxNameLength} characters: letters, numbers, spaces, - ' . _");

        var owner = await _db.Users.FirstOrDefaultAsync(u => u.Id == ownerUserId, cancellationToken);
        if (owner == null)
            return new TenantProvisionResult(TenantProvisionOutcome.UserNotFound, null, null, "User not found.");

        var maxOwned = settings.SelfServiceMaxOwnedTenants > 0 ? settings.SelfServiceMaxOwnedTenants : DefaultMaxOwned;
        var adminRole = TenantRole.TenantAdmin.ToString();
        var owned = await _db.TenantUsers
            .IgnoreQueryFilters()
            .Join(_db.Tenants.IgnoreQueryFilters().Where(t => t.IsActive), tu => tu.TenantId, t => t.Id, (tu, t) => tu)
            .CountAsync(tu => tu.UserId == ownerUserId && tu.JoinedAt != default && tu.RoleString == adminRole, cancellationToken);
        if (owned >= maxOwned)
            return new TenantProvisionResult(TenantProvisionOutcome.CapReached, null, null,
                $"You already administer {owned} map{(owned == 1 ? "" : "s")} - the limit is {maxOwned}. Ask a superadmin if you need more.");

        var quota = settings.SelfServiceDefaultQuotaMB > 0 ? settings.SelfServiceDefaultQuotaMB : DefaultQuotaMB;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Quota is server-decided; the request never carries one
        var tenant = await _tenantService.CreateTenantAsync(new CreateTenantDto { StorageQuotaMB = quota });

        if (name != null)
        {
            tenant = await _tenantService.UpdateTenantAsync(tenant.Id, new UpdateTenantDto { Name = name });
        }

        var membership = await _membership.AddMemberAsync(new AddMemberRequest
        {
            TenantId = tenant.Id,
            UserId = ownerUserId,
            Role = TenantRole.TenantAdmin,
            Permissions = InvitationPresets.FullPermissions,
            PerformedByUserId = ownerUserId,
            AuditAction = "TenantSelfCreated",
            JoinSource = MembershipJoinSources.SelfCreated,
            SetActiveTenant = true
        }, cancellationToken);

        if (!membership.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError("Self-service tenant {TenantId} could not get its owner membership ({Outcome})", tenant.Id, membership.Outcome);
            return new TenantProvisionResult(TenantProvisionOutcome.UserNotFound, null, null, "Could not create the map. Please try again.");
        }

        await transaction.CommitAsync(cancellationToken);

        // Directory skeleton is lazily (re)created on first upload as well - an IO hiccup here is not fatal
        try
        {
            var gridStorage = _configuration["GridStorage"] ?? "map";
            _filePaths.EnsureTenantDirectoriesExist(tenant.Id, gridStorage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pre-create storage directories for tenant {TenantId}", tenant.Id);
        }

        _logger.LogInformation("User {UserId} created tenant {TenantId} ({TenantName}) via self-service (quota {Quota} MB)",
            ownerUserId, tenant.Id, tenant.Name, quota);

        return new TenantProvisionResult(TenantProvisionOutcome.Created, tenant.Id, tenant.Name, null);
    }

    /// <summary>Trims/collapses whitespace and validates; null = use the generated id as the name.</summary>
    public static string? NormalizeName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var collapsed = WhitespaceRegex().Replace(displayName.Trim(), " ");
        if (collapsed.Length < MinNameLength || collapsed.Length > MaxNameLength)
            return null;
        if (!NamePattern().IsMatch(collapsed))
            return null;

        return collapsed;
    }
}
