using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Infrastructure.Repositories;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Multi-use, expiring invite links. Real SQLite because the redemption claim is a conditional ExecuteUpdate
/// and the back-filled legacy shape (MaxUses=1, UseCount=1, Status=Used) must keep behaving as single-use.
/// </summary>
public class InvitationServiceMultiUseTests : IDisposable
{
    private const string TenantId = "village-map-1";
    private const string OtherTenantId = "other-map-2";
    private const string InactiveTenantId = "sleeping-map-3";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly InvitationService _service;

    public InvitationServiceMultiUseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-invite-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
        Seed();

        _service = new InvitationService(new TenantInvitationRepository(_db), _db, Mock.Of<ILogger<InvitationService>>());
    }

    [Fact]
    public async Task Create_ClampsEveryOption()
    {
        var defaults = await _service.CreateInvitationAsync(TenantId, "admin");
        var odd = await _service.CreateInvitationAsync(TenantId, "admin", expiresInDays: 45, maxUses: 500, preset: "nonsense");
        var thirty = await _service.CreateInvitationAsync(TenantId, "admin", expiresInDays: 30, maxUses: 0, preset: InvitationPresets.Contribute);

        Assert.InRange(defaults.ExpiresAt, DateTime.UtcNow.AddDays(7).AddMinutes(-1), DateTime.UtcNow.AddDays(7).AddMinutes(1));
        Assert.Null(defaults.MaxUses);
        Assert.Equal(InvitationPresets.Full, defaults.Preset);
        Assert.Equal(5, defaults.Permissions.Count);
        Assert.Equal(0, defaults.UseCount);
        Assert.Equal(36, defaults.InviteCode.Length);   // Guid - never shortened

        Assert.InRange(odd.ExpiresAt, DateTime.UtcNow.AddDays(7).AddMinutes(-1), DateTime.UtcNow.AddDays(7).AddMinutes(1)); // 45 -> default
        Assert.Equal(100, odd.MaxUses);                  // ceiling
        Assert.Equal(InvitationPresets.Full, odd.Preset); // unknown preset -> Full, never empty

        Assert.InRange(thirty.ExpiresAt, DateTime.UtcNow.AddDays(30).AddMinutes(-1), DateTime.UtcNow.AddDays(30).AddMinutes(1));
        Assert.Null(thirty.MaxUses);                     // 0 = unlimited
        Assert.Equal(InvitationPresets.Contribute, thirty.Preset);
        Assert.Equal(4, thirty.Permissions.Count);
        Assert.DoesNotContain(Permission.Writer.ToString(), thirty.Permissions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_ReturnsPreviewForValidLinks_AndPreciseReasonsForDeadOnes()
    {
        var valid = await _service.CreateInvitationAsync(TenantId, "jorb", 30, 3, InvitationPresets.Contribute);
        var preview = await _service.ValidateInvitationAsync(valid.InviteCode);
        Assert.True(preview.IsValid);
        Assert.Equal(TenantId, preview.TenantId);
        Assert.Equal("Village Map", preview.TenantName);
        Assert.Equal("jorb", preview.InvitedBy);
        Assert.Equal(2, preview.MemberCount);            // approved members only - the pending one is not counted
        Assert.Equal(valid.ExpiresAt, preview.ExpiresAt);
        Assert.Equal(InvitationPresets.Contribute, preview.Preset);

        var unknown = await _service.ValidateInvitationAsync("does-not-exist");
        Assert.False(unknown.IsValid);
        Assert.Equal("Invitation is invalid or expired", unknown.ErrorMessage);
        Assert.Null(unknown.TenantId);                   // no existence oracle, no tenant leak

        var revokedLink = await _service.CreateInvitationAsync(TenantId, "jorb");
        await _service.RevokeInvitationAsync(revokedLink.Id, TenantId);
        Assert.Contains("revoked", (await _service.ValidateInvitationAsync(revokedLink.InviteCode)).ErrorMessage);

        var expired = SeedRaw(TenantId, "expired-code", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        Assert.Contains("expired", (await _service.ValidateInvitationAsync(expired.InviteCode)).ErrorMessage);

        var inactive = SeedRaw(InactiveTenantId, "inactive-code");
        Assert.Contains("no longer active", (await _service.ValidateInvitationAsync(inactive.InviteCode)).ErrorMessage);
    }

    [Fact]
    public async Task Claim_ExactlyOneWinnerForTheLastUse_AndStatusFlipsToUsed()
    {
        var link = await _service.CreateInvitationAsync(TenantId, "admin", maxUses: 1);

        var first = await _service.TryClaimUseAsync(link.InviteCode, "user-a");
        var second = await _service.TryClaimUseAsync(link.InviteCode, "user-b");

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, first!.UseCount);
        Assert.Equal("Used", first.Status);
        Assert.Equal("user-a", first.UsedBy);

        var validation = await _service.ValidateInvitationAsync(link.InviteCode);
        Assert.False(validation.IsValid);
        Assert.Contains("no remaining uses", validation.ErrorMessage);
    }

    [Fact]
    public async Task Claim_UnlimitedLinkStaysActiveAcrossManyUses()
    {
        var link = await _service.CreateInvitationAsync(TenantId, "admin");

        for (var i = 1; i <= 7; i++)
        {
            var claimed = await _service.TryClaimUseAsync(link.InviteCode, $"user-{i}");
            Assert.NotNull(claimed);
            Assert.Equal(i, claimed!.UseCount);
            Assert.Equal("Active", claimed.Status);
        }

        Assert.True((await _service.ValidateInvitationAsync(link.InviteCode)).IsValid);
    }

    [Fact]
    public async Task Claim_RefusesExpiredRevokedAndLegacyUsedLinks()
    {
        var expired = SeedRaw(TenantId, "expired-claim", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(await _service.TryClaimUseAsync(expired.InviteCode, "user-x"));

        var revokedLink = await _service.CreateInvitationAsync(TenantId, "admin");
        await _service.RevokeInvitationAsync(revokedLink.Id, TenantId);
        Assert.Null(await _service.TryClaimUseAsync(revokedLink.InviteCode, "user-x"));

        // Exactly what the migration back-fill produces for a link redeemed under the old single-use flow
        var legacy = SeedRaw(TenantId, "legacy-used", status: "Used", maxUses: 1, useCount: 1);
        Assert.Null(await _service.TryClaimUseAsync(legacy.InviteCode, "user-x"));
        Assert.Contains("no remaining uses", (await _service.ValidateInvitationAsync(legacy.InviteCode)).ErrorMessage);

        // ...and an un-redeemed legacy link (MaxUses back-filled to 1) still works exactly once
        var legacyActive = SeedRaw(TenantId, "legacy-active", maxUses: 1);
        Assert.NotNull(await _service.TryClaimUseAsync(legacyActive.InviteCode, "user-x"));
        Assert.Null(await _service.TryClaimUseAsync(legacyActive.InviteCode, "user-y"));
    }

    [Fact]
    public async Task Revoke_RequiresOwnership_AndIsIdempotent()
    {
        var link = await _service.CreateInvitationAsync(TenantId, "admin");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.RevokeInvitationAsync(link.Id, OtherTenantId));
        Assert.Equal("Active", (await _db.TenantInvitations.SingleAsync(i => i.Id == link.Id)).Status);

        await _service.RevokeInvitationAsync(link.Id, TenantId);
        await _service.RevokeInvitationAsync(link.Id, TenantId);   // second call is a no-op
        Assert.Equal("Revoked", (await _db.TenantInvitations.SingleAsync(i => i.Id == link.Id)).Status);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.RevokeInvitationAsync(999_999, TenantId));
    }

    [Fact]
    public async Task NewFlowNeverProducesTheLegacyPendingShape()
    {
        var link = await _service.CreateInvitationAsync(TenantId, "admin", maxUses: 2);
        await _service.TryClaimUseAsync(link.InviteCode, "user-a");
        await _service.TryClaimUseAsync(link.InviteCode, "user-b");

        // The legacy purge only looks at Status=Used AND PendingApproval=true rows - none can come from here
        var legacyCandidates = await _db.TenantInvitations.Where(i => i.Status == "Used" && i.PendingApproval).ToListAsync();
        Assert.Empty(legacyCandidates);
    }

    private TenantInvitationEntity SeedRaw(string tenantId, string code, DateTime? expiresAt = null, string status = "Active", int? maxUses = null, int useCount = 0)
    {
        var entity = new TenantInvitationEntity
        {
            TenantId = tenantId,
            InviteCode = code,
            CreatedBy = "seed",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(6),
            Status = status,
            MaxUses = maxUses,
            UseCount = useCount
        };
        _db.TenantInvitations.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;
        _db.Tenants.AddRange(
            new TenantEntity { Id = TenantId, Name = "Village Map", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = OtherTenantId, Name = "Other Map", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = InactiveTenantId, Name = "Sleeping Map", CreatedAt = now, IsActive = false });
        _db.Users.AddRange(
            new ApplicationUser { Id = "m1", UserName = "m1", NormalizedUserName = "M1" },
            new ApplicationUser { Id = "m2", UserName = "m2", NormalizedUserName = "M2" },
            new ApplicationUser { Id = "m3", UserName = "m3", NormalizedUserName = "M3" });
        _db.SaveChanges();

        _db.TenantUsers.AddRange(
            new TenantUserEntity { TenantId = TenantId, UserId = "m1", Role = TenantRole.TenantAdmin, JoinedAt = now },
            new TenantUserEntity { TenantId = TenantId, UserId = "m2", Role = TenantRole.TenantUser, JoinedAt = now },
            new TenantUserEntity { TenantId = TenantId, UserId = "m3", Role = TenantRole.TenantUser, JoinedAt = default }); // pending
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
