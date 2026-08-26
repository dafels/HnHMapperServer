using System.Security.Claims;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using HnHMapperServer.Services.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// The superadmin-managed sign-in settings. What matters: authorization is enforced by the STORE (SuperAdmin by
/// claim AND database role), secrets are encrypted at rest and only decrypted in the process that opts in,
/// deployment config only seeds defaults, blank secret fields keep the stored value, Clear flags remove it,
/// and every save refreshes the cache + writes an audit row that never contains a secret.
/// </summary>
public class AuthSettingsStoreTests : IDisposable
{
    private const string SuperAdminId = "user-sa";
    private const string PlainUserId = "user-plain";
    private const string ClaimOnlyId = "user-claim-only";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly AuthSettingsCache _cache = new();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    public AuthSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-authsettings-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();   // no migrations -> no "__global__" tenant row yet

        _db.Roles.Add(new IdentityRole { Id = "role-sa", Name = AuthorizationConstants.Roles.SuperAdmin, NormalizedName = "SUPERADMIN" });
        _db.Users.AddRange(
            new Infrastructure.Identity.ApplicationUser { Id = SuperAdminId, UserName = "root", NormalizedUserName = "ROOT" },
            new Infrastructure.Identity.ApplicationUser { Id = PlainUserId, UserName = "villager", NormalizedUserName = "VILLAGER" },
            new Infrastructure.Identity.ApplicationUser { Id = ClaimOnlyId, UserName = "demoted", NormalizedUserName = "DEMOTED" });
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = SuperAdminId, RoleId = "role-sa" });
        _db.SaveChanges();
    }

    private static ClaimsPrincipal Principal(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Name, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static readonly ClaimsPrincipal SuperAdmin = Principal(SuperAdminId, AuthorizationConstants.Roles.SuperAdmin);
    private static readonly ClaimsPrincipal PlainUser = Principal(PlainUserId, "TenantAdmin");
    /// <summary>Cookie still says SuperAdmin, but the role was revoked in the database.</summary>
    private static readonly ClaimsPrincipal ClaimOnly = Principal(ClaimOnlyId, AuthorizationConstants.Roles.SuperAdmin);

    private AuthSettingsStore Build(Dictionary<string, string?>? config = null, bool decryptSecrets = true, AuthSettingsCache? cache = null) =>
        new(_db, cache ?? _cache,
            new AuthSettingsStoreOptions { DecryptSecrets = decryptSecrets },
            new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build(),
            _dataProtection,
            new AuditService(_db, Mock.Of<IHttpContextAccessor>()),
            Mock.Of<ILogger<AuthSettingsStore>>());

    private static AuthSettingsUpdate Update(bool steam = true, string? steamKey = null, bool discord = true, string? discordId = "987654321", string? discordSecret = null) => new()
    {
        SelfRegistrationEnabled = true, SelfServiceTenantsEnabled = true, SelfServiceTenantsRequireExternalIdentity = true, SelfServiceDefaultQuotaMB = 1024, SelfServiceMaxOwnedTenants = 3,
        SteamEnabled = steam, SteamApplicationKey = steamKey, DiscordEnabled = discord, DiscordClientId = discordId, DiscordClientSecret = discordSecret
    };

    // ------------------------------------------------------------------ authorization

    [Fact]
    public async Task OnlySuperAdmins_CanViewOrSave_ByClaimAndByDatabase()
    {
        var store = Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetViewAsync(PlainUser));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(Update(), PlainUser));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetViewAsync(ClaimOnly));        // claim without DB role
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(Update(), ClaimOnly));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(Update(), new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Empty(await _db.Config.IgnoreQueryFilters().ToListAsync());   // nothing was written
        Assert.Empty(await _db.AuditLogs.ToListAsync());

        var view = await store.GetViewAsync(SuperAdmin);   // the real superadmin gets through
        Assert.NotNull(view);
    }

    [Fact]
    public async Task Policy_IsAvailableToAnyCaller_ButCarriesNoSecrets()
    {
        var store = Build();
        await store.SaveAsync(Update(steamKey: "steam-key", discordSecret: "discord-secret"), SuperAdmin);

        var policy = await store.GetPolicyAsync();   // no principal needed - flags only

        Assert.True(policy.SteamSignInEnabled);
        Assert.True(policy.DiscordSignInEnabled);
        Assert.True(policy.SelfRegistrationEnabled);
        Assert.DoesNotContain(typeof(AuthPolicy).GetProperties(), p => p.PropertyType == typeof(string));   // literally no string fields
    }

    // ------------------------------------------------------------------ defaults

    [Fact]
    public async Task Get_WithNothingSaved_UsesDeploymentConfigurationAsDefaults()
    {
        var store = Build(new()
        {
            ["SelfRegistration:Enabled"] = "false",
            ["TenantSelfService:MaxOwnedTenants"] = "7",
            ["Authentication:Steam:ApplicationKey"] = "seed-steam-key",
            ["Authentication:Discord:ClientId"] = "123",
            ["Authentication:Discord:ClientSecret"] = "shh"
        });

        var policy = await store.GetPolicyAsync();
        var view = await store.GetViewAsync(SuperAdmin);

        Assert.False(policy.SelfRegistrationEnabled);
        Assert.True(policy.SelfServiceTenantsEnabled);       // unset -> default true
        Assert.Equal(1024, policy.SelfServiceDefaultQuotaMB);
        Assert.Equal(7, policy.SelfServiceMaxOwnedTenants);
        Assert.True(policy.SteamSignInEnabled);               // a seeded key switches Steam on
        Assert.True(policy.DiscordSignInEnabled);
        Assert.True(view.SteamApplicationKeyConfigured);
        Assert.True(view.DiscordClientSecretConfigured);
        Assert.Equal("123", view.DiscordClientId);
        Assert.Equal("seed-steam-key", _cache.Current!.SteamApplicationKey);   // decrypted into THIS (web-like) process only
    }

    [Fact]
    public async Task IdentityRule_DefaultsOn_AndRoundTrips()
    {
        var store = Build();
        Assert.True((await store.GetPolicyAsync()).SelfServiceTenantsRequireExternalIdentity);   // default: password accounts cannot create maps

        var update = Update();
        update.SelfServiceTenantsRequireExternalIdentity = false;
        var saved = await store.SaveAsync(update, SuperAdmin);

        Assert.False(saved.SelfServiceTenantsRequireExternalIdentity);
        Assert.False((await store.GetPolicyAsync()).SelfServiceTenantsRequireExternalIdentity);
        Assert.Contains("externalOnly=False", (await _db.AuditLogs.SingleAsync()).NewValue);
    }

    [Fact]
    public async Task Get_WithNoConfigurationAtAll_HasSafeDefaults()
    {
        var policy = await Build().GetPolicyAsync();

        Assert.True(policy.SelfRegistrationEnabled);
        Assert.True(policy.SelfServiceTenantsEnabled);
        Assert.False(policy.SteamSignInEnabled);
        Assert.False(policy.DiscordSignInEnabled);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public async Task Save_PersistsEverything_EncryptsSecrets_AndOverridesConfiguration()
    {
        var store = Build(new() { ["SelfRegistration:Enabled"] = "true" });
        var changed = new List<AuthSettings>();
        _cache.Changed += changed.Add;

        var saved = await store.SaveAsync(new AuthSettingsUpdate
        {
            SelfRegistrationEnabled = false,
            SelfServiceTenantsEnabled = false,
            SelfServiceDefaultQuotaMB = 512,
            SelfServiceMaxOwnedTenants = 2,
            SteamEnabled = true,
            SteamApplicationKey = "  ABCDEF0123456789  ",
            DiscordEnabled = true,
            DiscordClientId = "987654321",
            DiscordClientSecret = "super-secret"
        }, SuperAdmin);

        Assert.False(saved.SelfRegistrationEnabled);
        Assert.True(saved.SteamApplicationKeyConfigured);
        Assert.True(saved.SteamActive);
        Assert.True(saved.DiscordActive);
        Assert.Single(changed);
        Assert.Equal("ABCDEF0123456789", _cache.Current!.SteamApplicationKey);   // trimmed, decrypted in-process

        // Stored rows: flags readable, secrets unreadable
        var rows = await _db.Config.IgnoreQueryFilters()
            .Where(c => c.TenantId == AuthSettingsStore.GlobalTenantId)
            .ToDictionaryAsync(c => c.Key, c => c.Value);
        Assert.Equal("False", rows[AuthSettingsStore.KeySelfRegistration]);
        Assert.Equal("987654321", rows[AuthSettingsStore.KeyDiscordClientId]);
        Assert.DoesNotContain("ABCDEF0123456789", rows[AuthSettingsStore.KeySteamApiKey]);
        Assert.DoesNotContain("super-secret", rows[AuthSettingsStore.KeyDiscordClientSecret]);
        Assert.StartsWith("dp1:", rows[AuthSettingsStore.KeySteamApiKey]);

        // The "__global__" pseudo-tenant was created on demand (Config rows carry an FK to Tenants)
        Assert.NotNull(await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == AuthSettingsStore.GlobalTenantId));

        // A fresh store reads the saved values back, decrypted, ignoring the configuration default
        var rereadCache = new AuthSettingsCache();
        var reread = Build(new() { ["SelfRegistration:Enabled"] = "true" }, cache: rereadCache);
        var policy = await reread.GetPolicyAsync();
        Assert.False(policy.SelfRegistrationEnabled);
        Assert.Equal(512, policy.SelfServiceDefaultQuotaMB);
        Assert.Equal("ABCDEF0123456789", rereadCache.Current!.SteamApplicationKey);
        Assert.Equal("super-secret", rereadCache.Current.DiscordClientSecret);

        // Audit row names the actor (from the principal) and describes the change without leaking secrets
        var audit = await _db.AuditLogs.SingleAsync(a => a.Action == "AuthSettingsUpdated");
        Assert.Equal(SuperAdminId, audit.UserId);
        Assert.Contains("steam=True (key set)", audit.NewValue);
        Assert.Contains("clientId=987654321", audit.NewValue);
        Assert.DoesNotContain("super-secret", audit.NewValue);
        Assert.DoesNotContain("ABCDEF0123456789", audit.NewValue);
    }

    [Fact]
    public async Task ApiProcess_NeverDecryptsSecrets_ButStillKnowsTheyExist()
    {
        await Build().SaveAsync(Update(steamKey: "steam-key", discordSecret: "discord-secret"), SuperAdmin);

        var apiCache = new AuthSettingsCache();
        var apiStore = Build(decryptSecrets: false, cache: apiCache);
        var policy = await apiStore.GetPolicyAsync();

        Assert.True(policy.DiscordSignInEnabled);               // presence is known...
        Assert.Null(apiCache.Current!.SteamApplicationKey);      // ...the plaintext is not in memory
        Assert.Null(apiCache.Current.DiscordClientSecret);
        Assert.True(apiCache.Current.DiscordSecretConfigured);
    }

    [Fact]
    public async Task Save_BlankSecretKeepsStoredValue_ClearFlagRemovesIt()
    {
        var store = Build();
        await store.SaveAsync(Update(steamKey: "steam-key", discordSecret: "secret"), SuperAdmin);

        // Blank secrets = keep (the UI never echoes secrets back into the form)
        var kept = await store.SaveAsync(Update(steamKey: "", discordSecret: null), SuperAdmin);
        Assert.True(kept.SteamApplicationKeyConfigured);
        Assert.True(kept.DiscordActive);
        Assert.Equal("steam-key", _cache.Current!.SteamApplicationKey);
        Assert.Equal("secret", _cache.Current.DiscordClientSecret);

        // Clear flags remove them; Discord then drops to inactive even though its switch is on
        var update = Update();
        update.ClearSteamApplicationKey = true;
        update.ClearDiscordClientSecret = true;
        var cleared = await store.SaveAsync(update, SuperAdmin);
        Assert.False(cleared.SteamApplicationKeyConfigured);
        Assert.True(cleared.SteamActive);                // Steam works without a key
        Assert.False(cleared.DiscordClientSecretConfigured);
        Assert.False(cleared.DiscordActive);
        Assert.True(cleared.DiscordEnabled);
        Assert.Null(_cache.Current!.DiscordClientSecret);
    }

    [Fact]
    public async Task Save_PersistsAConfigurationSeedOnFirstSave_SoTheUiBecomesTheSourceOfTruth()
    {
        var seeded = Build(new() { ["Authentication:Discord:ClientId"] = "seed-id", ["Authentication:Discord:ClientSecret"] = "seed-secret" });
        await seeded.SaveAsync(Update(discordId: "seed-id", discordSecret: null), SuperAdmin);   // blank = keep the seed

        // A process WITHOUT that configuration now reads the persisted (encrypted) seed
        var noConfigCache = new AuthSettingsCache();
        await Build(cache: noConfigCache).GetPolicyAsync();
        Assert.Equal("seed-secret", noConfigCache.Current!.DiscordClientSecret);
        Assert.True(noConfigCache.Current.DiscordActive);
    }

    [Fact]
    public async Task Save_ClampsNumbers()
    {
        var update = Update();
        update.SelfServiceDefaultQuotaMB = -5;
        update.SelfServiceMaxOwnedTenants = 0;

        var saved = await Build().SaveAsync(update, SuperAdmin);

        Assert.Equal(1, saved.SelfServiceDefaultQuotaMB);
        Assert.Equal(1, saved.SelfServiceMaxOwnedTenants);
    }

    [Fact]
    public async Task Get_ReturnsCachedSnapshotWithinTtl_AndSaveRefreshesItImmediately()
    {
        var store = Build();
        var first = await store.GetPolicyAsync();
        Assert.True(first.SelfRegistrationEnabled);

        var update = Update();
        update.SelfRegistrationEnabled = false;
        await store.SaveAsync(update, SuperAdmin);

        Assert.False((await store.GetPolicyAsync()).SelfRegistrationEnabled);   // visible right away
        Assert.Same(_cache.Current, _cache.Current);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
