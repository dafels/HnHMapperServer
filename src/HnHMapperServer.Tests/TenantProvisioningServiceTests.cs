using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Self-service "Create a new map": the tenant row, the owner's admin membership, the active tenant and the
/// storage directories must all appear together, with quota/cap/kill-switch decided by configuration only.
/// </summary>
public class TenantProvisioningServiceTests : IDisposable
{
    private const string OwnerId = "user-owner";

    private readonly string _dbPath;
    private readonly string _gridStorage;
    private readonly ApplicationDbContext _db;

    public TenantProvisioningServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-provision-test-{Guid.NewGuid():N}.db");
        _gridStorage = Path.Combine(Path.GetTempPath(), $"hnh-provision-storage-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.Users.Add(new ApplicationUser { Id = OwnerId, UserName = "founder", NormalizedUserName = "FOUNDER" });
        _db.SaveChanges();
    }

    private TenantProvisioningService Build(Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GridStorage"] = _gridStorage,
                ["TenantSelfService:DefaultStorageQuotaMB"] = "256",
                // The seeded owner is a password-only account; the identity rule has its own tests below
                ["TenantSelfService:RequireExternalIdentity"] = "false",
            }.Concat(settings ?? new()).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value))
            .Build();

        var tenantService = new TenantService(
            new TenantRepository(_db),
            new TenantNameService(_db, Mock.Of<ILogger<TenantNameService>>()),
            Mock.Of<ILogger<TenantService>>());
        var invitations = new InvitationService(new TenantInvitationRepository(_db), _db, Mock.Of<ILogger<InvitationService>>());
        var audit = new AuditService(_db, Mock.Of<IHttpContextAccessor>());
        var membership = new TenantMembershipService(_db, invitations, audit, Mock.Of<ILogger<TenantMembershipService>>());
        // No rows saved through the UI yet -> the store falls back to the configuration defaults above
        var settingsStore = new AuthSettingsStore(_db, new AuthSettingsCache(), new AuthSettingsStoreOptions { DecryptSecrets = false }, config, new EphemeralDataProtectionProvider(), audit, Mock.Of<ILogger<AuthSettingsStore>>());

        return new TenantProvisioningService(_db, tenantService, membership, new TenantFilePathService(), settingsStore, config, Mock.Of<ILogger<TenantProvisioningService>>());
    }

    [Fact]
    public async Task Create_MakesTheCallerAdminOfANewTenant_WithConfiguredQuota_ActiveTenant_Dirs_Audit()
    {
        var service = Build();

        var result = await service.CreateOwnedTenantAsync(OwnerId, "  Northwind   Village ");

        Assert.True(result.Succeeded);
        Assert.Equal("Northwind Village", result.TenantName);   // whitespace collapsed
        Assert.NotNull(result.TenantId);

        var tenant = await _db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == result.TenantId);
        Assert.Equal(256, tenant.StorageQuotaMB);               // from config, never from the caller
        Assert.True(tenant.IsActive);
        Assert.Matches(@"^[a-z0-9-]+-\d{4}$", tenant.Id);      // generated icon-icon-NNNN id

        var membership = await _db.TenantUsers.IgnoreQueryFilters().Include(tu => tu.Permissions)
            .SingleAsync(tu => tu.TenantId == tenant.Id && tu.UserId == OwnerId);
        Assert.Equal(TenantRole.TenantAdmin, membership.Role);
        Assert.NotEqual(default, membership.JoinedAt);
        Assert.Equal(MembershipJoinSources.SelfCreated, membership.JoinSource);
        Assert.Equal(5, membership.Permissions.Count);
        Assert.Contains(membership.Permissions, p => p.Permission == Permission.Writer);

        Assert.Equal(tenant.Id, (await _db.Users.SingleAsync(u => u.Id == OwnerId)).ActiveTenantId);
        Assert.True(Directory.Exists(Path.Combine(_gridStorage, "tenants", tenant.Id)));
        Assert.Single(await _db.AuditLogs.Where(a => a.Action == "TenantSelfCreated" && a.TenantId == tenant.Id).ToListAsync());
    }

    [Fact]
    public async Task Create_WithoutName_KeepsTheGeneratedIdAsName()
    {
        var result = await Build().CreateOwnedTenantAsync(OwnerId, null);

        Assert.True(result.Succeeded);
        Assert.Equal(result.TenantId, result.TenantName);
    }

    [Fact]
    public async Task Create_RejectsInvalidNames_WithoutCreatingAnything()
    {
        var service = Build();

        foreach (var bad in new[] { "ab", new string('x', 41), "<script>", "bellinside", "semi;colon" })
        {
            var result = await service.CreateOwnedTenantAsync(OwnerId, bad);
            Assert.Equal(TenantProvisionOutcome.InvalidName, result.Outcome);
        }

        Assert.Empty(await _db.Tenants.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await _db.TenantUsers.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Create_KillSwitchOff_CreatesNothing()
    {
        var service = Build(new() { ["TenantSelfService:Enabled"] = "false" });

        var result = await service.CreateOwnedTenantAsync(OwnerId, "Anything");

        Assert.Equal(TenantProvisionOutcome.Disabled, result.Outcome);
        Assert.False(await service.IsEnabledAsync());
        Assert.Empty(await _db.Tenants.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Create_EnforcesTheOwnedTenantCap_CountingOnlyActiveAdminMemberships()
    {
        var service = Build(new() { ["TenantSelfService:MaxOwnedTenants"] = "2" });

        Assert.True((await service.CreateOwnedTenantAsync(OwnerId, "First")).Succeeded);
        Assert.True((await service.CreateOwnedTenantAsync(OwnerId, "Second")).Succeeded);

        // Plain membership and a pending admin row do not count towards the cap
        _db.Tenants.Add(new TenantEntity { Id = "member-only", Name = "Member only", CreatedAt = DateTime.UtcNow, IsActive = true });
        _db.Tenants.Add(new TenantEntity { Id = "pending-admin", Name = "Pending", CreatedAt = DateTime.UtcNow, IsActive = true });
        _db.SaveChanges();
        _db.TenantUsers.AddRange(
            new TenantUserEntity { TenantId = "member-only", UserId = OwnerId, Role = TenantRole.TenantUser, JoinedAt = DateTime.UtcNow },
            new TenantUserEntity { TenantId = "pending-admin", UserId = OwnerId, Role = TenantRole.TenantAdmin, JoinedAt = default });
        _db.SaveChanges();

        var third = await service.CreateOwnedTenantAsync(OwnerId, "Third");
        Assert.Equal(TenantProvisionOutcome.CapReached, third.Outcome);
        Assert.Contains("limit is 2", third.Error);

        // A suspended tenant frees a slot
        var first = await _db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == "First");
        first.IsActive = false;
        await _db.SaveChangesAsync();
        Assert.True((await service.CreateOwnedTenantAsync(OwnerId, "Third")).Succeeded);
    }

    [Fact]
    public async Task Create_PasswordOnlyAccount_IsNotEligible_WhenTheIdentityRuleIsOn()
    {
        var service = Build(new() { ["TenantSelfService:RequireExternalIdentity"] = "true" });

        var options = await service.GetOptionsAsync(OwnerId);
        Assert.True(options.Enabled);
        Assert.False(options.Eligible);
        Assert.Contains("Steam or Discord", options.Reason);

        var result = await service.CreateOwnedTenantAsync(OwnerId, "Nope");
        Assert.Equal(TenantProvisionOutcome.NotEligible, result.Outcome);
        Assert.Empty(await _db.Tenants.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Create_AccountWithLinkedProvider_OrSuperAdmin_IsEligible_WhenTheIdentityRuleIsOn()
    {
        var service = Build(new() { ["TenantSelfService:RequireExternalIdentity"] = "true" });

        // A Steam login (at sign-up or linked later) is a verified identity
        _db.UserLogins.Add(new Microsoft.AspNetCore.Identity.IdentityUserLogin<string> { UserId = OwnerId, LoginProvider = "Steam", ProviderKey = "steam-1", ProviderDisplayName = "Steam" });
        _db.SaveChanges();
        Assert.True((await service.GetOptionsAsync(OwnerId)).Eligible);
        Assert.True((await service.CreateOwnedTenantAsync(OwnerId, "Verified")).Succeeded);

        // A password-only superadmin is always eligible
        _db.Users.Add(new ApplicationUser { Id = "user-root", UserName = "root", NormalizedUserName = "ROOT" });
        _db.Roles.Add(new Microsoft.AspNetCore.Identity.IdentityRole { Id = "role-sa", Name = AuthorizationConstants.Roles.SuperAdmin, NormalizedName = "SUPERADMIN" });
        _db.SaveChanges();
        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "user-root", RoleId = "role-sa" });
        _db.SaveChanges();
        Assert.True((await service.GetOptionsAsync("user-root")).Eligible);
        Assert.True((await service.CreateOwnedTenantAsync("user-root", "Root map")).Succeeded);
    }

    [Fact]
    public async Task Create_UnknownUser_CreatesNothing()
    {
        var result = await Build().CreateOwnedTenantAsync("ghost", "Ghost map");

        Assert.Equal(TenantProvisionOutcome.UserNotFound, result.Outcome);
        Assert.Empty(await _db.Tenants.IgnoreQueryFilters().ToListAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_gridStorage)) Directory.Delete(_gridStorage, recursive: true); } catch { /* best effort */ }
    }
}
