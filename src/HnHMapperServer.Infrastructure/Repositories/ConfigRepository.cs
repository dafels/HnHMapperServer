using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HnHMapperServer.Infrastructure.Repositories;

public class ConfigRepository : IConfigRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContextAccessor _tenantContext;

    public ConfigRepository(ApplicationDbContext context, ITenantContextAccessor tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<Config> GetConfigAsync()
    {
        var config = new Config();

        // Title is tenant-scoped
        var titleEntity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == "title");
        if (titleEntity != null)
            config.Title = titleEntity.Value;

        // Prefix is GLOBAL (system-wide setting)
        config.Prefix = await GetGlobalValueAsync("prefix") ?? string.Empty;

        // DefaultHide is tenant-scoped
        var defaultHideEntity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == "defaultHide");
        config.DefaultHide = defaultHideEntity != null;

        // MainMap is tenant-scoped
        var mainMapEntity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == "mainMap");
        if (mainMapEntity != null && int.TryParse(mainMapEntity.Value, out var mainMapId))
            config.MainMapId = mainMapId;

        // AllowGridUpdates is tenant-scoped (default true if not set)
        var allowGridUpdatesEntity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == "allowGridUpdates");
        config.AllowGridUpdates = allowGridUpdatesEntity == null || allowGridUpdatesEntity.Value == "true";

        // AllowNewMaps is tenant-scoped (default true if not set)
        var allowNewMapsEntity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == "allowNewMaps");
        config.AllowNewMaps = allowNewMapsEntity == null || allowNewMapsEntity.Value == "true";

        return config;
    }

    public async Task SaveConfigAsync(Config config)
    {
        // Title is tenant-scoped
        await SetValueAsync("title", config.Title);

        // Prefix is GLOBAL (system-wide setting)
        await SetGlobalValueAsync("prefix", config.Prefix);

        // DefaultHide is tenant-scoped
        if (config.DefaultHide)
        {
            await SetValueAsync("defaultHide", "true");
        }
        else
        {
            await DeleteValueAsync("defaultHide");
        }

        if (config.MainMapId.HasValue)
        {
            await SetValueAsync("mainMap", config.MainMapId.Value.ToString());
        }
        else
        {
            await DeleteValueAsync("mainMap");
        }

        // AllowGridUpdates is tenant-scoped (only store if false, default is true)
        if (!config.AllowGridUpdates)
        {
            await SetValueAsync("allowGridUpdates", "false");
        }
        else
        {
            await DeleteValueAsync("allowGridUpdates");
        }

        // AllowNewMaps is tenant-scoped (only store if false, default is true)
        if (!config.AllowNewMaps)
        {
            await SetValueAsync("allowNewMaps", "false");
        }
        else
        {
            await DeleteValueAsync("allowNewMaps");
        }
    }

    public async Task<string?> GetValueAsync(string key)
    {
        // IMPORTANT: FindAsync bypasses tenant filters - use explicit tenant check
        var currentTenantId = _tenantContext.GetRequiredTenantId();
        var entity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == key && c.TenantId == currentTenantId);
        return entity?.Value;
    }

    public async Task SetValueAsync(string key, string value)
    {
        // Config keys can be the same across tenants (e.g., "title", "prefix")
        // The PRIMARY KEY is (Key, TenantId), so each tenant can have their own config
        var currentTenantId = _tenantContext.GetRequiredTenantId();

        // Check if config key exists for current tenant (uses global query filter automatically)
        var existing = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == key && c.TenantId == currentTenantId);

        if (existing != null)
        {
            // Update existing config for this tenant
            existing.Value = value;
            await _context.SaveChangesAsync();
        }
        else
        {
            // Insert new config for this tenant
            _context.Config.Add(new ConfigEntity
            {
                Key = key,
                Value = value,
                TenantId = currentTenantId
            });
            await _context.SaveChangesAsync();
        }
    }

    public async Task DeleteValueAsync(string key)
    {
        // IMPORTANT: FindAsync bypasses tenant filters - use explicit tenant check
        var currentTenantId = _tenantContext.GetRequiredTenantId();
        var entity = await _context.Config
            .FirstOrDefaultAsync(c => c.Key == key && c.TenantId == currentTenantId);
        if (entity != null)
        {
            _context.Config.Remove(entity);
            await _context.SaveChangesAsync();
        }
    }

    // Global config methods (not tenant-scoped)
    // Use reserved TenantId "__global__" for system-wide settings

    /// <summary>
    /// Get a global configuration value (not tenant-scoped).
    /// Used for system-wide settings like URL prefix.
    /// FALLBACK: If __global__ doesn't exist, returns first value from any tenant.
    /// </summary>
    public async Task<string?> GetGlobalValueAsync(string key)
    {
        // Try to get from __global__ tenant first
        var entity = await _context.Config
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == key && c.TenantId == "__global__");

        if (entity != null)
            return entity.Value;

        // FALLBACK: If __global__ doesn't exist yet, get from any tenant
        // This provides backwards compatibility during migration
        var fallback = await _context.Config
            .IgnoreQueryFilters()
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync();

        return fallback;
    }

    /// <summary>
    /// Set a global configuration value (not tenant-scoped).
    /// Used for system-wide settings like URL prefix.
    /// </summary>
    public async Task SetGlobalValueAsync(string key, string value)
    {
        // Bypass tenant filter and explicitly work with global config
        var existing = await _context.Config
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == key && c.TenantId == "__global__");

        if (existing != null)
        {
            existing.Value = value;
            await _context.SaveChangesAsync();
        }
        else
        {
            _context.Config.Add(new ConfigEntity
            {
                Key = key,
                Value = value,
                TenantId = "__global__"
            });
            await _context.SaveChangesAsync();
        }
    }
}
