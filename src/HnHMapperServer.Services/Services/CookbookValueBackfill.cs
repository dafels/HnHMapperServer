using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// One-time repair of every tenant's canonical food values.
///
/// Until 2026-08-26 the bundled wiki dump outranked the game client when a food row was
/// created, so most foods carried wiki numbers even in tenants whose entire catalog came
/// from client uploads — the client's real values went to FoodVariants and nothing ever
/// promoted them. New uploads fix a food when it is next reported, but a food nobody
/// handles again would stay wrong forever, so every existing row is repaired once from
/// the observations already on file. Nothing is deleted and no re-upload is involved.
/// </summary>
public class CookbookValueBackfill : ICookbookValueBackfill
{
    /// <summary>Pseudo-tenant holding cross-tenant Config rows (see AuthSettingsStore).</summary>
    private const string GlobalTenantId = "__global__";

    /// <summary>Marker key; the value is the highest backfill version that has run.</summary>
    private const string MarkerKey = "cookbook.valueBackfillVersion";

    /// <summary>Bump only to force a re-run of the repair on the next start.</summary>
    private const int CurrentVersion = 1;

    private readonly ApplicationDbContext _dbContext;
    private readonly IFoodCatalogService _foodCatalog;
    private readonly IAuditService _auditService;
    private readonly ILogger<CookbookValueBackfill> _logger;

    public CookbookValueBackfill(
        ApplicationDbContext dbContext,
        IFoodCatalogService foodCatalog,
        IAuditService auditService,
        ILogger<CookbookValueBackfill> logger)
    {
        _dbContext = dbContext;
        _foodCatalog = foodCatalog;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<CookbookValueBackfillResult> RunOnceAsync(CancellationToken ct = default)
    {
        var result = new CookbookValueBackfillResult();

        var marker = await _dbContext.Config.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == GlobalTenantId && c.Key == MarkerKey, ct);
        if (marker != null
            && int.TryParse(marker.Value, out var applied)
            && applied >= CurrentVersion)
        {
            result.AlreadyApplied = true;
            return result;
        }

        var tenantIds = await _dbContext.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id != GlobalTenantId)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var refresh = await _foodCatalog.RefreshCanonicalValuesAsync(tenantId, ct);
                result.TenantsProcessed++;
                result.Foods += refresh.Foods;
                result.Updated += refresh.Updated;

                if (refresh.Updated > 0)
                {
                    await _auditService.LogAsync(new AuditEntry
                    {
                        TenantId = tenantId,
                        Action = "CookbookValuesRefreshed",
                        EntityType = "FoodCatalog",
                        NewValue = $"one-time repair: {refresh.Updated} of {refresh.Foods} foods "
                                   + $"({refresh.FromUploads} from client uploads, {refresh.FromImports} from imports, "
                                   + $"{refresh.LeftOnWiki} with no recorded observation)"
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad tenant must not block the rest; the marker is withheld so the
                // whole pass runs again on the next start.
                result.TenantsFailed++;
                _logger.LogError(ex, "Cookbook value backfill failed for tenant {TenantId}", tenantId);
            }
        }

        if (result.TenantsFailed == 0)
        {
            if (marker == null)
            {
                // Config rows carry an FK to Tenants; the pseudo-tenant is seeded by the
                // ConsolidateGlobalConfig migration but is missing in databases created
                // without migrations (same guard as AuthSettingsStore).
                await EnsureGlobalTenantAsync(ct);
                _dbContext.Config.Add(new ConfigEntity
                {
                    TenantId = GlobalTenantId,
                    Key = MarkerKey,
                    Value = CurrentVersion.ToString()
                });
            }
            else
            {
                marker.Value = CurrentVersion.ToString();
            }

            await _dbContext.SaveChangesAsync(ct);
            result.MarkerWritten = true;
        }

        return result;
    }

    private async Task EnsureGlobalTenantAsync(CancellationToken ct)
    {
        if (await _dbContext.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == GlobalTenantId, ct))
        {
            return;
        }

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = GlobalTenantId,
            Name = "Global System Settings",
            StorageQuotaMB = 0,
            CurrentStorageMB = 0,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        await _dbContext.SaveChangesAsync(ct);
    }
}
