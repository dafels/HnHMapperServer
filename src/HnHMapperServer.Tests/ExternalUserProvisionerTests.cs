using HnHMapperServer.Core.Constants;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Account provisioning/linking for external sign-in, against a real UserManager + UserStore over SQLite
/// (AspNetUserLogins uniqueness and the "never strand an account" rule are what matter here).
/// </summary>
public class ExternalUserProvisionerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ExternalUserProvisioner _provisioner;

    public ExternalUserProvisionerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-external-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        var store = new UserStore<ApplicationUser, IdentityRole, ApplicationDbContext>(_db);
        _userManager = new UserManager<ApplicationUser>(
            store,
            // Same relaxed password policy as the app (6+ chars, nothing else)
            Options.Create(new IdentityOptions
            {
                Password = { RequireDigit = false, RequireLowercase = false, RequireUppercase = false, RequireNonAlphanumeric = false, RequiredLength = 6 }
            }),
            new PasswordHasher<ApplicationUser>(),
            new IUserValidator<ApplicationUser>[] { new UserValidator<ApplicationUser>() },
            new IPasswordValidator<ApplicationUser>[] { new PasswordValidator<ApplicationUser>() },
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            Mock.Of<ILogger<UserManager<ApplicationUser>>>());

        _provisioner = new ExternalUserProvisioner(
            _userManager,
            new AuditService(_db, Mock.Of<IHttpContextAccessor>()),
            Mock.Of<ILogger<ExternalUserProvisioner>>());
    }

    [Fact]
    public async Task Provision_CreatesPasswordlessAccount_WithSourceLoginAndAudit()
    {
        var steam = new ExternalIdentity("Steam", "https://steamcommunity.com/openid/id/76561198000000001", "Jorb the Wanderer");

        var user = await _provisioner.ProvisionAsync(steam);

        Assert.Equal("Jorb_the_Wanderer", user.UserName);
        Assert.Equal(RegistrationSources.Steam, user.RegistrationSource);
        Assert.Null(user.DiscordName);
        Assert.False(await _userManager.HasPasswordAsync(user));
        Assert.NotNull(user.CreatedAt);

        var logins = await _userManager.GetLoginsAsync(user);
        Assert.Single(logins);
        Assert.Equal("Steam", logins[0].LoginProvider);
        Assert.Equal(steam.ProviderKey, logins[0].ProviderKey);

        Assert.Same(user.Id, (await _provisioner.FindAsync(steam))!.Id);
        Assert.Single(await _db.AuditLogs.Where(a => a.Action == "ExternalUserProvisioned" && a.UserId == user.Id).ToListAsync());
    }

    [Fact]
    public async Task Provision_Discord_SetsVerifiedDiscordName_AndUniquifiesUsernames()
    {
        Assert.True((await _userManager.CreateAsync(new ApplicationUser { UserName = "loftar", Email = string.Empty }, "password123")).Succeeded);

        var user = await _provisioner.ProvisionAsync(new ExternalIdentity("Discord", "123456789012345678", "loftar"));

        Assert.Equal("loftar_2", user.UserName);           // username taken by the password account
        Assert.Equal("loftar", user.DiscordName);          // the real Discord username, not self-reported
        Assert.Equal(RegistrationSources.Discord, user.RegistrationSource);
    }

    [Fact]
    public async Task Find_ReturnsNull_ForUnknownIdentity()
    {
        Assert.Null(await _provisioner.FindAsync(new ExternalIdentity("Steam", "nobody", null)));
    }

    [Fact]
    public async Task Link_AttachesProviderToExistingAccount_AndRefusesConflicts()
    {
        var passwordUser = new ApplicationUser { UserName = "veteran", Email = string.Empty };
        Assert.True((await _userManager.CreateAsync(passwordUser, "password123")).Succeeded);
        var other = await _provisioner.ProvisionAsync(new ExternalIdentity("Steam", "steam-other", "Other"));

        var linked = await _provisioner.LinkAsync(passwordUser, new ExternalIdentity("Discord", "discord-veteran", "Veteran#1"));
        Assert.Equal(LinkOutcome.Linked, linked);
        Assert.Equal("Veteran#1", passwordUser.DiscordName);   // filled because it was empty
        Assert.Same(passwordUser.Id, (await _provisioner.FindAsync(new ExternalIdentity("Discord", "discord-veteran", null)))!.Id);

        // Same identity again: idempotent
        Assert.Equal(LinkOutcome.Linked, await _provisioner.LinkAsync(passwordUser, new ExternalIdentity("Discord", "discord-veteran", null)));

        // A second, different Discord identity on the same account
        Assert.Equal(LinkOutcome.ProviderAlreadyLinked, await _provisioner.LinkAsync(passwordUser, new ExternalIdentity("Discord", "discord-second", null)));

        // An identity owned by another account can never be re-pointed
        Assert.Equal(LinkOutcome.LinkedToAnotherAccount, await _provisioner.LinkAsync(passwordUser, new ExternalIdentity("Steam", "steam-other", null)));
        Assert.Same(other.Id, (await _provisioner.FindAsync(new ExternalIdentity("Steam", "steam-other", null)))!.Id);

        Assert.Single(await _db.AuditLogs.Where(a => a.Action == "ExternalAccountLinked").ToListAsync());
    }

    [Fact]
    public async Task Unlink_NeverStrandsAnAccount()
    {
        var steamOnly = await _provisioner.ProvisionAsync(new ExternalIdentity("Steam", "steam-only", "Solo"));
        Assert.Equal(UnlinkOutcome.LastSignInMethod, await _provisioner.UnlinkAsync(steamOnly, "Steam"));
        Assert.Single(await _userManager.GetLoginsAsync(steamOnly));

        // With a second provider the first can go...
        Assert.Equal(LinkOutcome.Linked, await _provisioner.LinkAsync(steamOnly, new ExternalIdentity("Discord", "discord-solo", "Solo")));
        Assert.Equal(2, (await _userManager.GetLoginsAsync(steamOnly)).Count);
        Assert.Equal(UnlinkOutcome.Unlinked, await _provisioner.UnlinkAsync(steamOnly, "Steam"));
        Assert.Equal(UnlinkOutcome.NotLinked, await _provisioner.UnlinkAsync(steamOnly, "Steam"));

        // ...but the last one stays unless a password exists
        Assert.Equal(UnlinkOutcome.LastSignInMethod, await _provisioner.UnlinkAsync(steamOnly, "Discord"));
        await _userManager.AddPasswordAsync(steamOnly, "password123");
        Assert.Equal(UnlinkOutcome.Unlinked, await _provisioner.UnlinkAsync(steamOnly, "Discord"));
        Assert.Empty(await _userManager.GetLoginsAsync(steamOnly));
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
