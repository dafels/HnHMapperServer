using System.Security.Claims;
using HnHMapperServer.Core.Constants;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

public class AuthSettingsStore : IAuthSettingsStore
{
    public const string GlobalTenantId = "__global__";
    public const string ProtectorPurpose = "HnHMapperServer.AuthSettings.v1";
    private const string ProtectedPrefix = "dp1:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    // Config keys (global rows)
    public const string KeySelfRegistration = "auth.selfRegistration";
    public const string KeySelfServiceTenants = "auth.selfServiceTenants";
    public const string KeySelfServiceRequireExternal = "auth.selfServiceRequireExternalIdentity";
    public const string KeySelfServiceQuota = "auth.selfServiceQuotaMB";
    public const string KeySelfServiceMaxOwned = "auth.selfServiceMaxOwned";
    public const string KeySteamEnabled = "auth.steam.enabled";
    public const string KeySteamApiKey = "auth.steam.apiKey";
    public const string KeyDiscordEnabled = "auth.discord.enabled";
    public const string KeyDiscordClientId = "auth.discord.clientId";
    public const string KeyDiscordClientSecret = "auth.discord.clientSecret";

    private static readonly string[] AllKeys =
    {
        KeySelfRegistration, KeySelfServiceTenants, KeySelfServiceRequireExternal, KeySelfServiceQuota, KeySelfServiceMaxOwned,
        KeySteamEnabled, KeySteamApiKey, KeyDiscordEnabled, KeyDiscordClientId, KeyDiscordClientSecret
    };

    private readonly ApplicationDbContext _db;
    private readonly AuthSettingsCache _cache;
    private readonly AuthSettingsStoreOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IDataProtector _protector;
    private readonly IAuditService _audit;
    private readonly ILogger<AuthSettingsStore> _logger;

    public AuthSettingsStore(
        ApplicationDbContext db,
        AuthSettingsCache cache,
        AuthSettingsStoreOptions options,
        IConfiguration configuration,
        IDataProtectionProvider dataProtection,
        IAuditService audit,
        ILogger<AuthSettingsStore> logger)
    {
        _db = db;
        _cache = cache;
        _options = options;
        _configuration = configuration;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _audit = audit;
        _logger = logger;
    }

    public async Task<AuthPolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        (await GetCurrentAsync(cancellationToken)).ToPolicy();

    public Task WarmAsync(CancellationToken cancellationToken = default) => GetCurrentAsync(cancellationToken);

    public async Task<AuthSettingsView> GetViewAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        await RequireSuperAdminAsync(caller, cancellationToken);
        return AuthSettingsView.From(await LoadAsync(cancellationToken));
    }

    public async Task<AuthSettingsView> SaveAsync(AuthSettingsUpdate update, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        var actorUserId = await RequireSuperAdminAsync(caller, cancellationToken);

        var before = await LoadAsync(cancellationToken);
        var next = before.Clone();

        next.SelfRegistrationEnabled = update.SelfRegistrationEnabled;
        next.SelfServiceTenantsEnabled = update.SelfServiceTenantsEnabled;
        next.SelfServiceTenantsRequireExternalIdentity = update.SelfServiceTenantsRequireExternalIdentity;
        next.SelfServiceDefaultQuotaMB = Math.Clamp(update.SelfServiceDefaultQuotaMB, 1, 1_000_000);
        next.SelfServiceMaxOwnedTenants = Math.Clamp(update.SelfServiceMaxOwnedTenants, 1, 1000);

        next.SteamEnabled = update.SteamEnabled;
        next.DiscordEnabled = update.DiscordEnabled;
        next.DiscordClientId = string.IsNullOrWhiteSpace(update.DiscordClientId) ? null : update.DiscordClientId.Trim();

        await EnsureGlobalTenantAsync(cancellationToken);

        var rows = await _db.Config
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == GlobalTenantId && AllKeys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, cancellationToken);

        Upsert(rows, KeySelfRegistration, next.SelfRegistrationEnabled.ToString());
        Upsert(rows, KeySelfServiceTenants, next.SelfServiceTenantsEnabled.ToString());
        Upsert(rows, KeySelfServiceRequireExternal, next.SelfServiceTenantsRequireExternalIdentity.ToString());
        Upsert(rows, KeySelfServiceQuota, next.SelfServiceDefaultQuotaMB.ToString());
        Upsert(rows, KeySelfServiceMaxOwned, next.SelfServiceMaxOwnedTenants.ToString());
        Upsert(rows, KeySteamEnabled, next.SteamEnabled.ToString());
        UpsertSecret(rows, KeySteamApiKey, update.SteamApplicationKey, update.ClearSteamApplicationKey, before.SteamApplicationKey);
        Upsert(rows, KeyDiscordEnabled, next.DiscordEnabled.ToString());
        Upsert(rows, KeyDiscordClientId, next.DiscordClientId ?? string.Empty);
        UpsertSecret(rows, KeyDiscordClientSecret, update.DiscordClientSecret, update.ClearDiscordClientSecret, before.DiscordClientSecret);

        await _db.SaveChangesAsync(cancellationToken);

        // Re-read so the "configured" markers and (in the Web process) the decrypted values are exact
        var saved = await LoadAsync(cancellationToken);
        _cache.Set(saved, raiseChanged: true);

        await _audit.LogAsync(new AuditEntry
        {
            UserId = actorUserId,
            Action = "AuthSettingsUpdated",
            EntityType = "AuthSettings",
            EntityId = "global",
            OldValue = Describe(before),
            NewValue = Describe(saved)
        });

        _logger.LogInformation("Sign-in/onboarding settings updated by {UserId}: {Summary}", actorUserId, Describe(saved));
        return AuthSettingsView.From(saved);
    }

    // ------------------------------------------------------------------ internals

    private async Task<AuthSettings> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var current = _cache.Current;
        if (current != null && DateTime.UtcNow - _cache.LoadedAt < CacheTtl)
            return current;

        var loaded = await LoadAsync(cancellationToken);
        _cache.Set(loaded, raiseChanged: false);
        return loaded;
    }

    /// <summary>
    /// SuperAdmin by claim AND by database role (so a revoked superadmin is refused even with a still-valid
    /// cookie). Returns the caller's user id for the audit row.
    /// </summary>
    private async Task<string> RequireSuperAdminAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var userId = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId) || caller.Identity?.IsAuthenticated != true
            || !caller.IsInRole(AuthorizationConstants.Roles.SuperAdmin))
        {
            _logger.LogWarning("Refused sign-in settings access for {User}: not a SuperAdmin by claim", caller.Identity?.Name ?? "anonymous");
            throw new UnauthorizedAccessException("Only superadmins can view or change sign-in settings.");
        }

        var isSuperAdminInDb = await _db.UserRoles
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .AnyAsync(x => x.UserId == userId && x.Name == AuthorizationConstants.Roles.SuperAdmin, cancellationToken);
        if (!isSuperAdminInDb)
        {
            _logger.LogWarning("Refused sign-in settings access for user {UserId}: SuperAdmin role not present in the database", userId);
            throw new UnauthorizedAccessException("Only superadmins can view or change sign-in settings.");
        }

        return userId;
    }

    private async Task<AuthSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.Config
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == GlobalTenantId && AllKeys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, cancellationToken);

        // Deployment configuration supplies the defaults for anything not saved through the UI yet
        var configSteamKey = NullIfBlank(_configuration["Authentication:Steam:ApplicationKey"]);
        var configDiscordId = NullIfBlank(_configuration["Authentication:Discord:ClientId"]);
        var configDiscordSecret = NullIfBlank(_configuration["Authentication:Discord:ClientSecret"]);

        var steamStored = rows.TryGetValue(KeySteamApiKey, out var steamRaw);
        var discordSecretStored = rows.TryGetValue(KeyDiscordClientSecret, out var discordSecretRaw);

        var settings = new AuthSettings
        {
            SelfRegistrationEnabled = Bool(rows, KeySelfRegistration, _configuration.GetValue<bool?>("SelfRegistration:Enabled") ?? true),
            SelfServiceTenantsEnabled = Bool(rows, KeySelfServiceTenants, _configuration.GetValue<bool?>("TenantSelfService:Enabled") ?? true),
            SelfServiceTenantsRequireExternalIdentity = Bool(rows, KeySelfServiceRequireExternal, _configuration.GetValue<bool?>("TenantSelfService:RequireExternalIdentity") ?? true),
            SelfServiceDefaultQuotaMB = Int(rows, KeySelfServiceQuota, _configuration.GetValue<int?>("TenantSelfService:DefaultStorageQuotaMB") ?? 1024),
            SelfServiceMaxOwnedTenants = Int(rows, KeySelfServiceMaxOwned, _configuration.GetValue<int?>("TenantSelfService:MaxOwnedTenants") ?? 3),
            SteamEnabled = Bool(rows, KeySteamEnabled, configSteamKey != null),
            DiscordEnabled = Bool(rows, KeyDiscordEnabled, configDiscordId != null && configDiscordSecret != null),
            DiscordClientId = rows.TryGetValue(KeyDiscordClientId, out var id) ? NullIfBlank(id) : configDiscordId
        };

        // Secrets: presence is always known; the plaintext is materialised only where DecryptSecrets is on
        if (steamStored)
        {
            settings.SteamKeyConfigured = !string.IsNullOrEmpty(steamRaw);
            settings.SteamApplicationKey = _options.DecryptSecrets ? Unprotect(steamRaw!, KeySteamApiKey) : null;
            if (_options.DecryptSecrets && settings.SteamKeyConfigured && settings.SteamApplicationKey == null)
                settings.SteamKeyConfigured = false;   // undecryptable = effectively unset
        }
        else
        {
            settings.SteamKeyConfigured = configSteamKey != null;
            settings.SteamApplicationKey = _options.DecryptSecrets ? configSteamKey : null;
        }

        if (discordSecretStored)
        {
            settings.DiscordSecretConfigured = !string.IsNullOrEmpty(discordSecretRaw);
            settings.DiscordClientSecret = _options.DecryptSecrets ? Unprotect(discordSecretRaw!, KeyDiscordClientSecret) : null;
            if (_options.DecryptSecrets && settings.DiscordSecretConfigured && settings.DiscordClientSecret == null)
                settings.DiscordSecretConfigured = false;
        }
        else
        {
            settings.DiscordSecretConfigured = configDiscordSecret != null;
            settings.DiscordClientSecret = _options.DecryptSecrets ? configDiscordSecret : null;
        }

        if (settings.SelfServiceDefaultQuotaMB < 1) settings.SelfServiceDefaultQuotaMB = 1024;
        if (settings.SelfServiceMaxOwnedTenants < 1) settings.SelfServiceMaxOwnedTenants = 3;
        return settings;
    }

    /// <summary>
    /// Config rows carry an FK to Tenants; the "__global__" pseudo-tenant is normally seeded by the
    /// ConsolidateGlobalConfig migration, but a database created without migrations (tests) lacks it.
    /// </summary>
    private async Task EnsureGlobalTenantAsync(CancellationToken cancellationToken)
    {
        var exists = await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == GlobalTenantId, cancellationToken);
        if (exists)
            return;

        _db.Tenants.Add(new TenantEntity
        {
            Id = GlobalTenantId,
            Name = "Global System Settings",
            StorageQuotaMB = 0,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void Upsert(Dictionary<string, ConfigEntity> rows, string key, string value)
    {
        if (rows.TryGetValue(key, out var row))
        {
            row.Value = value;
            return;
        }
        var entity = new ConfigEntity { Key = key, TenantId = GlobalTenantId, Value = value };
        _db.Config.Add(entity);
        rows[key] = entity;
    }

    /// <summary>
    /// Secret write rules: the Clear flag stores an empty row (unset); a new value is stored encrypted; blank
    /// ("keep" - the UI never echoes secrets back) leaves a stored row untouched, or persists a configuration seed
    /// the first time so the UI becomes the source of truth. With nothing stored and nothing known, no row is
    /// written and the configuration seed keeps applying.
    /// </summary>
    private void UpsertSecret(Dictionary<string, ConfigEntity> rows, string key, string? newValue, bool clear, string? currentPlaintext)
    {
        if (clear)
        {
            Upsert(rows, key, string.Empty);
            return;
        }
        if (!string.IsNullOrWhiteSpace(newValue))
        {
            Upsert(rows, key, Protect(newValue.Trim()));
            return;
        }
        if (rows.ContainsKey(key))
            return;
        if (!string.IsNullOrEmpty(currentPlaintext))
            Upsert(rows, key, Protect(currentPlaintext));
    }

    private string Protect(string? secret) =>
        string.IsNullOrWhiteSpace(secret) ? string.Empty : ProtectedPrefix + _protector.Protect(secret);

    private string? Unprotect(string stored, string key)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return stored;   // tolerate a value seeded by hand
        try
        {
            return _protector.Unprotect(stored[ProtectedPrefix.Length..]);
        }
        catch (Exception ex)
        {
            // Key ring changed (DataProtection-Keys directory lost) - the secret must be re-entered in the UI
            _logger.LogWarning(ex, "Could not decrypt stored secret {Key}; treating it as unset", key);
            return null;
        }
    }

    private static bool Bool(Dictionary<string, string> rows, string key, bool fallback) =>
        rows.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    private static int Int(Dictionary<string, string> rows, string key, int fallback) =>
        rows.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Audit-safe description: flags and ids only, secrets reduced to set/unset.</summary>
    private static string Describe(AuthSettings s) =>
        $"selfRegistration={s.SelfRegistrationEnabled}; selfServiceTenants={s.SelfServiceTenantsEnabled} (externalOnly={s.SelfServiceTenantsRequireExternalIdentity}); " +
        $"quotaMB={s.SelfServiceDefaultQuotaMB}; maxOwned={s.SelfServiceMaxOwnedTenants}; " +
        $"steam={s.SteamEnabled} (key {(s.SteamKeyConfigured ? "set" : "unset")}); " +
        $"discord={s.DiscordEnabled} (clientId={s.DiscordClientId ?? "unset"}, secret {(s.DiscordSecretConfigured ? "set" : "unset")})";
}
