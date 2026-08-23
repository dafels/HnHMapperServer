using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// The membership service is the single writer of TenantUsers rows. Real SQLite: the unique (TenantId, UserId)
/// index, the FK cascade to permissions, ExecuteUpdate in the invitation claim and the transaction around
/// redemption are all exercised for real. No IHttpContextAccessor/tenant context - exactly like a brand-new
/// account joining its first tenant.
/// </summary>
public class TenantMembershipServiceTests : IDisposable
{
    private const string TenantId = "village-map-1";
    private const string InactiveTenantId = "sleeping-map-2";
    private const string UserId = "user-new";
    private const string AdminId = "user-admin";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly InvitationService _invitations;
    private readonly TenantMembershipService _service;

    public TenantMembershipServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-membership-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
        Seed();

        _invitations = new InvitationService(new TenantInvitationRepository(_db), _db, Mock.Of<ILogger<InvitationService>>());
        var audit = new AuditService(_db, Mock.Of<IHttpContextAccessor>());
        _service = new TenantMembershipService(_db, _invitations, audit, Mock.Of<ILogger<TenantMembershipService>>());
    }

    // ------------------------------------------------------------------ AddMemberAsync

    [Fact]
    public async Task AddMember_CreatesApprovedMembershipWithExactPermissionsAndAuditRow()
    {
        var result = await _service.AddMemberAsync(new AddMemberRequest
        {
            TenantId = TenantId,
            UserId = UserId,
            Role = TenantRole.TenantUser,
            Permissions = new[] { Permission.Map, Permission.Upload, Permission.Map }, // duplicate on purpose
            PerformedByUserId = AdminId,
            AuditAction = "UserJoinedTenant",
            JoinSource = MembershipJoinSources.AdminAssigned,
            SetActiveTenant = true
        });

        Assert.Equal(MembershipOutcome.Joined, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal("Village Map", result.TenantName);
        Assert.Equal(new[] { Permission.Map, Permission.Upload }, result.Permissions.OrderBy(p => p));

        var row = await _db.TenantUsers.IgnoreQueryFilters().Include(tu => tu.Permissions)
            .SingleAsync(tu => tu.TenantId == TenantId && tu.UserId == UserId);
        Assert.NotEqual(default, row.JoinedAt);
        Assert.False(row.PendingApproval);
        Assert.Equal(TenantRole.TenantUser, row.Role);
        Assert.Equal(MembershipJoinSources.AdminAssigned, row.JoinSource);
        Assert.Null(row.InvitationId);
        Assert.Equal(2, row.Permissions.Count);

        var user = await _db.Users.SingleAsync(u => u.Id == UserId);
        Assert.Equal(TenantId, user.ActiveTenantId);

        var audit = await _db.AuditLogs.SingleAsync(a => a.Action == "UserJoinedTenant");
        Assert.Equal(AdminId, audit.UserId);       // actor is an Identity user id, never a username
        Assert.Equal(TenantId, audit.TenantId);
        Assert.Equal(UserId, audit.EntityId);
        Assert.Contains("source=AdminAssigned", audit.NewValue);
    }

    [Fact]
    public async Task AddMember_ApprovesLegacyPendingRowInPlace_MergesPermissions_ClearsInvitationFlag()
    {
        // Legacy shape: register-with-invite created a pending row and burned a single-use invitation.
        var pending = new TenantUserEntity { TenantId = TenantId, UserId = UserId, Role = TenantRole.TenantUser, JoinedAt = default };
        pending.Permissions.Add(new TenantPermissionEntity { Permission = Permission.Map });
        _db.TenantUsers.Add(pending);
        _db.TenantInvitations.Add(new TenantInvitationEntity
        {
            TenantId = TenantId, InviteCode = "legacy-code", CreatedBy = "admin", CreatedAt = DateTime.UtcNow.AddDays(-3),
            ExpiresAt = DateTime.UtcNow.AddDays(4), Status = "Used", UsedBy = UserId, UsedAt = DateTime.UtcNow.AddDays(-3),
            PendingApproval = true, MaxUses = 1, UseCount = 1
        });
        await _db.SaveChangesAsync();

        var result = await _service.AddMemberAsync(new AddMemberRequest
        {
            TenantId = TenantId,
            UserId = UserId,
            Permissions = new[] { Permission.Map, Permission.Markers },
            PerformedByUserId = AdminId,
            AuditAction = "UserApproved",
            JoinSource = MembershipJoinSources.Approved
        });

        Assert.Equal(MembershipOutcome.ApprovedExisting, result.Outcome);

        var row = await _db.TenantUsers.IgnoreQueryFilters().Include(tu => tu.Permissions)
            .SingleAsync(tu => tu.TenantId == TenantId && tu.UserId == UserId);
        Assert.NotEqual(default, row.JoinedAt);
        Assert.Equal(MembershipJoinSources.Approved, row.JoinSource);
        Assert.Equal(2, row.Permissions.Count); // Map not duplicated, Markers added

        var invitation = await _db.TenantInvitations.SingleAsync(i => i.InviteCode == "legacy-code");
        Assert.False(invitation.PendingApproval);   // the 7-day purge must leave this user alone now

        Assert.Single(await _db.TenantUsers.IgnoreQueryFilters().Where(tu => tu.UserId == UserId).ToListAsync());
    }

    [Fact]
    public async Task AddMember_AlreadyMember_ChangesNothingExceptActiveTenant()
    {
        await _service.AddMemberAsync(Request(Permission.Map));
        var auditCountAfterFirst = await _db.AuditLogs.CountAsync();

        var result = await _service.AddMemberAsync(new AddMemberRequest
        {
            TenantId = TenantId, UserId = UserId, Permissions = new[] { Permission.Writer },
            PerformedByUserId = UserId, AuditAction = "InvitationRedeemed", JoinSource = MembershipJoinSources.InviteLink,
            SetActiveTenant = true
        });

        Assert.Equal(MembershipOutcome.AlreadyMember, result.Outcome);
        Assert.Equal(new[] { Permission.Map }, result.Permissions);   // Writer was NOT granted
        Assert.Equal(auditCountAfterFirst, await _db.AuditLogs.CountAsync());
        Assert.Single(await _db.TenantPermissions.ToListAsync());
        Assert.Equal(TenantId, (await _db.Users.SingleAsync(u => u.Id == UserId)).ActiveTenantId);
    }

    [Fact]
    public async Task AddMember_RefusesUnknownOrInactiveTenants()
    {
        var missing = await _service.AddMemberAsync(Request(Permission.Map, tenantId: "no-such-map"));
        var inactive = await _service.AddMemberAsync(Request(Permission.Map, tenantId: InactiveTenantId));

        Assert.Equal(MembershipOutcome.TenantNotFound, missing.Outcome);
        Assert.Equal(MembershipOutcome.TenantInactive, inactive.Outcome);
        Assert.False(missing.Succeeded);
        Assert.False(inactive.Succeeded);
        Assert.Empty(await _db.TenantUsers.IgnoreQueryFilters().Where(tu => tu.UserId == UserId).ToListAsync());
    }

    // ------------------------------------------------------------------ RedeemInvitationAsync

    [Fact]
    public async Task Redeem_JoinsWithTheLinksPreset_AlwaysAsTenantUser_AndRecordsTheInvitation()
    {
        var link = await _invitations.CreateInvitationAsync(TenantId, "admin", 30, maxUses: 5, preset: InvitationPresets.Contribute);

        var result = await _service.RedeemInvitationAsync(link.InviteCode, UserId);

        Assert.Equal(RedeemOutcome.Joined, result.Outcome);
        Assert.Equal(TenantId, result.TenantId);
        Assert.Equal(4, result.Permissions.Count);
        Assert.DoesNotContain(Permission.Writer, result.Permissions);

        var row = await _db.TenantUsers.IgnoreQueryFilters().Include(tu => tu.Permissions)
            .SingleAsync(tu => tu.TenantId == TenantId && tu.UserId == UserId);
        Assert.Equal(TenantRole.TenantUser, row.Role);
        Assert.Equal(MembershipJoinSources.InviteLink, row.JoinSource);
        Assert.Equal(link.Id, row.InvitationId);
        Assert.Equal(4, row.Permissions.Count);

        var invitation = await _db.TenantInvitations.SingleAsync(i => i.Id == link.Id);
        Assert.Equal(1, invitation.UseCount);
        Assert.Equal(UserId, invitation.UsedBy);
        Assert.Equal("Active", invitation.Status);       // 4 uses left
        Assert.False(invitation.PendingApproval);         // new flow never produces the legacy pending shape

        Assert.Equal(TenantId, (await _db.Users.SingleAsync(u => u.Id == UserId)).ActiveTenantId);
        Assert.Single(await _db.AuditLogs.Where(a => a.Action == "InvitationRedeemed").ToListAsync());
    }

    [Fact]
    public async Task Redeem_DefaultPreset_GrantsAllFivePermissions()
    {
        var link = await _invitations.CreateInvitationAsync(TenantId, "admin");

        var result = await _service.RedeemInvitationAsync(link.InviteCode, UserId);

        Assert.Equal(RedeemOutcome.Joined, result.Outcome);
        Assert.Equal(5, result.Permissions.Count);
        Assert.Contains(Permission.Writer, result.Permissions);
    }

    [Fact]
    public async Task Redeem_AlreadyMember_IsIdempotentAndConsumesNoUse()
    {
        var link = await _invitations.CreateInvitationAsync(TenantId, "admin", maxUses: 1);
        await _service.AddMemberAsync(Request(Permission.Map));

        var result = await _service.RedeemInvitationAsync(link.InviteCode, UserId);

        Assert.Equal(RedeemOutcome.AlreadyMember, result.Outcome);
        Assert.True(result.Succeeded);
        var invitation = await _db.TenantInvitations.SingleAsync(i => i.Id == link.Id);
        Assert.Equal(0, invitation.UseCount);
        Assert.Equal("Active", invitation.Status);
    }

    [Fact]
    public async Task Redeem_RejectsUnknownExhaustedAndRevokedLinks_WithoutCreatingMemberships()
    {
        var single = await _invitations.CreateInvitationAsync(TenantId, "admin", maxUses: 1);
        Assert.Equal(RedeemOutcome.Joined, (await _service.RedeemInvitationAsync(single.InviteCode, "user-first")).Outcome);

        var exhausted = await _service.RedeemInvitationAsync(single.InviteCode, UserId);
        Assert.Equal(RedeemOutcome.Invalid, exhausted.Outcome);
        Assert.Contains("no remaining uses", exhausted.Error);

        var revokedLink = await _invitations.CreateInvitationAsync(TenantId, "admin");
        await _invitations.RevokeInvitationAsync(revokedLink.Id, TenantId);
        var revoked = await _service.RedeemInvitationAsync(revokedLink.InviteCode, UserId);
        Assert.Equal(RedeemOutcome.Invalid, revoked.Outcome);
        Assert.Contains("revoked", revoked.Error);

        var unknown = await _service.RedeemInvitationAsync("not-a-real-code", UserId);
        Assert.Equal(RedeemOutcome.Invalid, unknown.Outcome);
        Assert.Equal("Invitation is invalid or expired", unknown.Error);

        Assert.Empty(await _db.TenantUsers.IgnoreQueryFilters().Where(tu => tu.UserId == UserId).ToListAsync());
    }

    private static AddMemberRequest Request(Permission permission, string tenantId = TenantId) => new()
    {
        TenantId = tenantId,
        UserId = UserId,
        Permissions = new[] { permission },
        PerformedByUserId = UserId,
        AuditAction = "UserJoinedTenant",
        JoinSource = MembershipJoinSources.InviteLink
    };

    private void Seed()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            new ApplicationUser { Id = UserId, UserName = "newcomer", NormalizedUserName = "NEWCOMER" },
            new ApplicationUser { Id = AdminId, UserName = "admin", NormalizedUserName = "ADMIN" },
            new ApplicationUser { Id = "user-first", UserName = "first", NormalizedUserName = "FIRST" });
        _db.Tenants.AddRange(
            new TenantEntity { Id = TenantId, Name = "Village Map", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = InactiveTenantId, Name = "Sleeping Map", CreatedAt = now, IsActive = false });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
