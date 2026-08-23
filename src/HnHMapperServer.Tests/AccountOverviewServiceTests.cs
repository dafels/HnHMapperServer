using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Tests;

/// <summary>
/// The superadmin "who joined how" overview: sign-in methods derived from the Identity tables, registration
/// source, memberships with join source, filters, paging and the summary counts.
/// </summary>
public class AccountOverviewServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly AccountOverviewService _service;

    public AccountOverviewServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-overview-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
        Seed();
        _service = new AccountOverviewService(_db);
    }

    [Fact]
    public async Task Overview_DerivesSignInMethods_Source_AndMemberships()
    {
        var page = await _service.GetAccountsAsync(new AccountOverviewQuery { PageSize = 50 });

        Assert.Equal(5, page.Total);
        Assert.Equal(5, page.Items.Count);

        var pwd = page.Items.Single(a => a.Username == "password_only");
        Assert.Equal(new[] { "Password" }, pwd.SignInMethods);
        Assert.Equal(RegistrationSources.Password, pwd.RegistrationSource);
        Assert.Empty(pwd.ExternalIds);

        var steam = page.Items.Single(a => a.Username == "steam_only");
        Assert.Equal(new[] { "Steam" }, steam.SignInMethods);
        Assert.Equal(RegistrationSources.Steam, steam.RegistrationSource);
        Assert.Equal("steam-key-1", steam.ExternalIds["Steam"]);

        var both = page.Items.Single(a => a.Username == "linked_both");
        Assert.Equal(new[] { "Password", "Discord", "Steam" }, both.SignInMethods);
        Assert.Equal(RegistrationSources.Password, both.RegistrationSource);   // linked later, still a password registration
        Assert.True(both.IsSuperAdmin);

        var discord = page.Items.Single(a => a.Username == "discord_only");
        Assert.Equal("DiscordPersona", discord.DiscordName);

        var membership = Assert.Single(steam.Memberships);
        Assert.Equal("map-a", membership.TenantId);
        Assert.Equal("Map A", membership.TenantName);
        Assert.Equal("TenantUser", membership.Role);
        Assert.Equal(MembershipJoinSources.InviteLink, membership.JoinSource);
        Assert.Equal(42, membership.InvitationId);
        Assert.False(membership.Pending);

        var bothMemberships = both.Memberships.OrderBy(m => m.TenantId).ToList();
        Assert.Equal(2, bothMemberships.Count);
        Assert.Equal(MembershipJoinSources.SelfCreated, bothMemberships[0].JoinSource);
        Assert.Equal("TenantAdmin", bothMemberships[0].Role);
        Assert.True(bothMemberships[1].Pending);
    }

    [Fact]
    public async Task Overview_SummaryCountsAgreeWithTheRows()
    {
        var page = await _service.GetAccountsAsync(new AccountOverviewQuery { PageSize = 50 });
        var s = page.Summary;

        Assert.Equal(5, s.Total);
        Assert.Equal(2, s.ByRegistrationSource[RegistrationSources.Password]);
        Assert.Equal(2, s.ByRegistrationSource[RegistrationSources.Steam]);
        Assert.Equal(1, s.ByRegistrationSource[RegistrationSources.Discord]);
        Assert.Equal(2, s.BySignInMethod["Password"]);
        Assert.Equal(3, s.BySignInMethod["Steam"]);     // steam_only, linked_both, steam_nomap
        Assert.Equal(2, s.BySignInMethod["Discord"]);   // discord_only, linked_both
        Assert.Equal(3, s.WithoutTenant);               // password_only (no rows), discord_only (suspended map only), steam_nomap

        Assert.Equal(s.Total, page.Items.Count);
        Assert.Equal(s.WithoutTenant, page.Items.Count(a => !a.Memberships.Any(m => !m.Pending && m.TenantActive)));
    }

    [Fact]
    public async Task Overview_Filters_ByMethod_Source_Tenant_NoTenant_AndSearch()
    {
        var byMethod = await _service.GetAccountsAsync(new AccountOverviewQuery { Method = "Discord" });
        Assert.Equal(new[] { "discord_only", "linked_both" }, byMethod.Items.Select(a => a.Username).OrderBy(u => u));

        var bySource = await _service.GetAccountsAsync(new AccountOverviewQuery { Source = RegistrationSources.Steam });
        Assert.Equal(new[] { "steam_nomap", "steam_only" }, bySource.Items.Select(a => a.Username).OrderBy(u => u));

        var noTenant = await _service.GetAccountsAsync(new AccountOverviewQuery { NoTenant = true });
        Assert.Equal(new[] { "discord_only", "password_only", "steam_nomap" }, noTenant.Items.Select(a => a.Username).OrderBy(u => u));

        var inMapA = await _service.GetAccountsAsync(new AccountOverviewQuery { TenantId = "map-a" });
        Assert.Equal(new[] { "linked_both", "steam_only" }, inMapA.Items.Select(a => a.Username).OrderBy(u => u));

        var search = await _service.GetAccountsAsync(new AccountOverviewQuery { Search = "PERSONA" });   // Discord name, case-insensitive
        Assert.Equal(new[] { "discord_only" }, search.Items.Select(a => a.Username));

        var combined = await _service.GetAccountsAsync(new AccountOverviewQuery { Method = "Steam", NoTenant = true });
        Assert.Equal(new[] { "steam_nomap" }, combined.Items.Select(a => a.Username));
        Assert.Equal(1, combined.Total);
    }

    [Fact]
    public async Task Overview_PagesAndClampsPageSize()
    {
        var first = await _service.GetAccountsAsync(new AccountOverviewQuery { Page = 1, PageSize = 2 });
        var second = await _service.GetAccountsAsync(new AccountOverviewQuery { Page = 2, PageSize = 2 });
        var third = await _service.GetAccountsAsync(new AccountOverviewQuery { Page = 3, PageSize = 2 });
        var huge = await _service.GetAccountsAsync(new AccountOverviewQuery { Page = 0, PageSize = 5000 });

        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Single(third.Items);
        Assert.Empty(first.Items.Select(a => a.Id).Intersect(second.Items.Select(a => a.Id)));
        Assert.Equal(100, huge.PageSize);
        Assert.Equal(1, huge.Page);
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;

        _db.Roles.Add(new IdentityRole { Id = "role-sa", Name = AuthorizationConstants.Roles.SuperAdmin, NormalizedName = AuthorizationConstants.Roles.SuperAdmin.ToUpperInvariant() });

        ApplicationUser User(string id, string name, string source, bool password, DateTime created, string? discord = null) => new()
        {
            Id = id, UserName = name, NormalizedUserName = name.ToUpperInvariant(), RegistrationSource = source,
            PasswordHash = password ? "hash" : null, CreatedAt = created, DiscordName = discord
        };

        _db.Users.AddRange(
            User("u-pwd", "password_only", RegistrationSources.Password, true, now.AddDays(-5)),
            User("u-steam", "steam_only", RegistrationSources.Steam, false, now.AddDays(-4)),
            User("u-discord", "discord_only", RegistrationSources.Discord, false, now.AddDays(-3), "DiscordPersona"),
            User("u-both", "linked_both", RegistrationSources.Password, true, now.AddDays(-2)),
            User("u-steam-nomap", "steam_nomap", RegistrationSources.Steam, false, now.AddDays(-1)));

        _db.UserLogins.AddRange(
            new IdentityUserLogin<string> { UserId = "u-steam", LoginProvider = "Steam", ProviderKey = "steam-key-1", ProviderDisplayName = "Steam" },
            new IdentityUserLogin<string> { UserId = "u-discord", LoginProvider = "Discord", ProviderKey = "discord-key-1", ProviderDisplayName = "Discord" },
            new IdentityUserLogin<string> { UserId = "u-both", LoginProvider = "Steam", ProviderKey = "steam-key-2", ProviderDisplayName = "Steam" },
            new IdentityUserLogin<string> { UserId = "u-both", LoginProvider = "Discord", ProviderKey = "discord-key-2", ProviderDisplayName = "Discord" },
            new IdentityUserLogin<string> { UserId = "u-steam-nomap", LoginProvider = "Steam", ProviderKey = "steam-key-3", ProviderDisplayName = "Steam" });

        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = "u-both", RoleId = "role-sa" });

        _db.Tenants.AddRange(
            new TenantEntity { Id = "map-a", Name = "Map A", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = "map-b", Name = "Map B", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = "map-zzz", Name = "Suspended", CreatedAt = now, IsActive = false });
        _db.SaveChanges();

        _db.TenantUsers.AddRange(
            new TenantUserEntity { TenantId = "map-a", UserId = "u-steam", Role = TenantRole.TenantUser, JoinedAt = now.AddDays(-4), JoinSource = MembershipJoinSources.InviteLink, InvitationId = 42 },
            new TenantUserEntity { TenantId = "map-a", UserId = "u-both", Role = TenantRole.TenantAdmin, JoinedAt = now.AddDays(-2), JoinSource = MembershipJoinSources.SelfCreated },
            new TenantUserEntity { TenantId = "map-b", UserId = "u-both", Role = TenantRole.TenantUser, JoinedAt = default, JoinSource = MembershipJoinSources.Legacy },
            new TenantUserEntity { TenantId = "map-zzz", UserId = "u-discord", Role = TenantRole.TenantUser, JoinedAt = now.AddDays(-3), JoinSource = MembershipJoinSources.InviteLink });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
