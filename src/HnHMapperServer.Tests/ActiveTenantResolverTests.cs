using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Tests;

/// <summary>
/// The resolver decides which membership's claims land in the auth cookie. These tests pin the selection
/// rules both claims factories (Web + API) depend on: persisted ActiveTenantId wins while valid, pending
/// rows never count, suspended tenants never count, and the fallback is deterministic.
/// Real SQLite: the join against Tenants and the ordering are what is under test.
/// </summary>
public class ActiveTenantResolverTests : IDisposable
{
    private const string UserId = "user-1";
    private const string OlderTenant = "older-tenant-1";
    private const string NewerTenant = "newer-tenant-2";
    private const string SuspendedTenant = "suspended-tenant-3";
    private const string PendingTenant = "pending-tenant-4";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;

    public ActiveTenantResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-resolver-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;

        // No IHttpContextAccessor: the factory runs during cookie (re)validation where no tenant context exists.
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
        Seed();
    }

    [Fact]
    public async Task Resolve_PrefersPersistedActiveTenant_WhenStillAnApprovedMember()
    {
        var result = await ActiveTenantMembershipResolver.ResolveAsync(_db, UserId, NewerTenant);

        Assert.NotNull(result.Membership);
        Assert.Equal(NewerTenant, result.Membership!.TenantId);
        Assert.Equal(TenantRole.TenantUser, result.Membership.Role);
        Assert.Equal(new[] { Permission.Map }, result.Permissions);
    }

    [Fact]
    public async Task Resolve_FallsBackToOldestMembership_WhenNoActiveTenantPersisted()
    {
        var result = await ActiveTenantMembershipResolver.ResolveAsync(_db, UserId, activeTenantId: null);

        Assert.Equal(OlderTenant, result.Membership!.TenantId);
        Assert.Equal(TenantRole.TenantAdmin, result.Membership.Role);
        Assert.Equal(2, result.Permissions.Count);
        Assert.Contains(Permission.Writer, result.Permissions);
    }

    [Fact]
    public async Task Resolve_FallsBack_WhenActiveTenantIsStale()
    {
        // Points at a tenant the user was never a member of, a suspended tenant, and a pending membership:
        // all three must be ignored in favour of the deterministic fallback.
        foreach (var stale in new[] { "no-such-tenant", SuspendedTenant, PendingTenant })
        {
            var result = await ActiveTenantMembershipResolver.ResolveAsync(_db, UserId, stale);
            Assert.Equal(OlderTenant, result.Membership!.TenantId);

            var tenantOnly = await ActiveTenantMembershipResolver.ResolveTenantIdAsync(_db, UserId, stale);
            Assert.Equal(OlderTenant, tenantOnly);
        }
    }

    [Fact]
    public async Task Resolve_NeverSelectsPendingOrSuspendedMemberships()
    {
        // Remove the two valid memberships: only pending + suspended remain -> nothing is active.
        await _db.TenantUsers.IgnoreQueryFilters()
            .Where(tu => tu.UserId == UserId && (tu.TenantId == OlderTenant || tu.TenantId == NewerTenant))
            .ExecuteDeleteAsync();

        var result = await ActiveTenantMembershipResolver.ResolveAsync(_db, UserId, PendingTenant);

        Assert.Null(result.Membership);
        Assert.Empty(result.Permissions);
        Assert.Null(await ActiveTenantMembershipResolver.ResolveTenantIdAsync(_db, UserId, SuspendedTenant));
    }

    [Fact]
    public async Task Resolve_UnknownUser_ReturnsNone()
    {
        var result = await ActiveTenantMembershipResolver.ResolveAsync(_db, "nobody", OlderTenant);

        Assert.Null(result.Membership);
        Assert.Empty(result.Permissions);
    }

    [Fact]
    public async Task Resolve_PermissionsAreScopedToTheSelectedMembership()
    {
        var older = await ActiveTenantMembershipResolver.ResolveAsync(_db, UserId, OlderTenant);
        var newer = await ActiveTenantMembershipResolver.ResolveAsync(_db, UserId, NewerTenant);

        Assert.Equal(2, older.Permissions.Count);
        Assert.Single(newer.Permissions);
        Assert.DoesNotContain(Permission.Writer, newer.Permissions);
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;

        _db.Users.Add(new ApplicationUser { Id = UserId, UserName = "hearthling", NormalizedUserName = "HEARTHLING" });

        _db.Tenants.AddRange(
            new TenantEntity { Id = OlderTenant, Name = "Older", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = NewerTenant, Name = "Newer", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = SuspendedTenant, Name = "Suspended", CreatedAt = now, IsActive = false },
            new TenantEntity { Id = PendingTenant, Name = "Pending", CreatedAt = now, IsActive = true });
        _db.SaveChanges();

        var older = new TenantUserEntity { TenantId = OlderTenant, UserId = UserId, Role = TenantRole.TenantAdmin, JoinedAt = now.AddDays(-10) };
        var newer = new TenantUserEntity { TenantId = NewerTenant, UserId = UserId, Role = TenantRole.TenantUser, JoinedAt = now.AddDays(-1) };
        var suspended = new TenantUserEntity { TenantId = SuspendedTenant, UserId = UserId, Role = TenantRole.TenantAdmin, JoinedAt = now.AddDays(-20) };
        var pending = new TenantUserEntity { TenantId = PendingTenant, UserId = UserId, Role = TenantRole.TenantUser, JoinedAt = default };
        _db.TenantUsers.AddRange(older, newer, suspended, pending);
        _db.SaveChanges();

        _db.TenantPermissions.AddRange(
            new TenantPermissionEntity { TenantUserId = older.Id, Permission = Permission.Map },
            new TenantPermissionEntity { TenantUserId = older.Id, Permission = Permission.Writer },
            new TenantPermissionEntity { TenantUserId = newer.Id, Permission = Permission.Map },
            new TenantPermissionEntity { TenantUserId = suspended.Id, Permission = Permission.Writer });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
