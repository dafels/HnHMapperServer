using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Service for managing tenant storage quotas: atomic delta UPDATEs plus the authoritative
/// filesystem reconciliation (see <see cref="IStorageQuotaService"/> for the contract).
/// </summary>
public class StorageQuotaService : IStorageQuotaService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<StorageQuotaService> _logger;

    public StorageQuotaService(ApplicationDbContext db, ILogger<StorageQuotaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Checks if a tenant has enough quota for an upload. Reads the row (AsNoTracking): a tenant
    /// entity tracked earlier in the request (token validation) is a stale snapshot.
    /// </summary>
    public async Task<bool> CheckQuotaAsync(string tenantId, double sizeMB)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.IsActive, t.CurrentStorageMB, t.StorageQuotaMB })
            .FirstOrDefaultAsync();

        if (tenant == null)
        {
            _logger.LogWarning("CheckQuota: Tenant {TenantId} not found", tenantId);
            return false;
        }

        if (!tenant.IsActive)
        {
            _logger.LogWarning("CheckQuota: Tenant {TenantId} is not active", tenantId);
            return false;
        }

        var wouldExceed = tenant.CurrentStorageMB + sizeMB > tenant.StorageQuotaMB;

        if (wouldExceed)
        {
            _logger.LogWarning(
                "CheckQuota: Tenant {TenantId} over quota. Current: {Current}MB, Quota: {Quota}MB, Upload: {Upload}MB",
                tenantId, tenant.CurrentStorageMB, tenant.StorageQuotaMB, sizeMB);
        }

        return !wouldExceed;
    }

    /// <summary>
    /// Adds a (possibly negative) delta to the tenant's usage in one UPDATE, clamped at 0. Never a
    /// read-modify-write of a tracked TenantEntity: the request scope (token validation), a
    /// background job's all-tenant list or a superadmin edit may hold a stale snapshot, and saving
    /// snapshot + delta silently dropped every concurrent writer's delta. Like every ExecuteUpdate
    /// it does not refresh a tracked copy, and it does not save the caller's pending changes —
    /// every caller flushes its own before charging.
    /// </summary>
    public async Task IncrementStorageUsageAsync(string tenantId, double sizeMB)
    {
        if (sizeMB == 0)
        {
            return;
        }

        var rows = await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => t.CurrentStorageMB,
                t => t.CurrentStorageMB + sizeMB > 0 ? t.CurrentStorageMB + sizeMB : 0.0));

        if (rows == 0)
        {
            throw new InvalidOperationException($"Tenant {tenantId} not found");
        }

        _logger.LogDebug(
            "IncrementStorage: Tenant {TenantId} usage changed by {SizeMB}MB",
            tenantId, sizeMB);
    }

    /// <summary>
    /// <see cref="IncrementStorageUsageAsync"/> with the sign flipped (same single atomic UPDATE).
    /// </summary>
    public Task DecrementStorageUsageAsync(string tenantId, double sizeMB)
        => IncrementStorageUsageAsync(tenantId, -sizeMB);

    /// <summary>
    /// Gets the current storage usage for a tenant
    /// </summary>
    public async Task<double> GetCurrentUsageAsync(string tenantId)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        return tenant?.CurrentStorageMB ?? 0;
    }

    /// <summary>
    /// Gets the storage quota limit for a tenant
    /// </summary>
    public async Task<double> GetQuotaLimitAsync(string tenantId)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId);

        return tenant?.StorageQuotaMB ?? 0;
    }

    /// <summary>
    /// Recalculates storage usage from filesystem and updates database. The admin teardowns
    /// (wipe, purge, map delete, integrity purge) call this right after deleting data, so the
    /// drift it measures is mostly their own deletion: it is logged, never warned about.
    /// </summary>
    public async Task<double> RecalculateStorageUsageAsync(string tenantId, string gridStorage)
        => (await ReconcileStorageUsageAsync(tenantId, gridStorage)).CurrentMB;

    /// <summary>
    /// The authoritative reset of CurrentStorageMB to what is on disk under tenants/{t}/ (every
    /// file: grid pool, per-map legacy pyramids and imported zoom-0 PNGs, the WebP pyramid,
    /// .storage.json, leftover temp files). The previous value is read fresh from the row, not
    /// from a tracked entity, so the reported drift is real. Increments landing between that read
    /// and the UPDATE (one scan) are overwritten; the next run corrects them.
    /// </summary>
    public async Task<StorageReconciliation> ReconcileStorageUsageAsync(string tenantId, string gridStorage)
    {
        var before = await _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.CurrentStorageMB, t.StorageQuotaMB })
            .FirstOrDefaultAsync();

        if (before == null)
        {
            throw new InvalidOperationException($"Tenant {tenantId} not found");
        }

        // Calculate from filesystem (single pass - count and sum in one enumeration)
        var tenantDir = Path.Combine(gridStorage, "tenants", tenantId);
        var (fileCount, totalSizeBytes) = CalculateDirectorySizeAndCount(tenantDir);
        var totalSizeMB = totalSizeBytes / 1024.0 / 1024.0;

        await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CurrentStorageMB, totalSizeMB));
        SyncTrackedUsage(tenantId, totalSizeMB);

        // Write .storage.json file
        await WriteStorageMetadataAsync(tenantDir, tenantId, totalSizeBytes, totalSizeMB, fileCount);

        var reconciliation = new StorageReconciliation(before.CurrentStorageMB, totalSizeMB, before.StorageQuotaMB, fileCount);
        _logger.LogInformation(
            "RecalculateStorage: Tenant {TenantId} - Files: {FileCount}, Size: {SizeMB:F2}MB, Tracked: {TrackedMB:F2}MB, Drift: {DriftMB:F2}MB",
            tenantId, fileCount, totalSizeMB, reconciliation.PreviousMB, reconciliation.DriftMB);

        return reconciliation;
    }

    /// <summary>
    /// ExecuteUpdate bypasses the change tracker: a tenant entity this scope already tracks would
    /// keep the old value (and a later SaveChanges of that entity used to write it back). Both the
    /// current and the original value are set and the property is not marked modified, so the
    /// entry stays Unchanged unless the caller changed other columns.
    /// </summary>
    private void SyncTrackedUsage(string tenantId, double currentMB)
    {
        // Snapshot first: changing a property value fires tracker events that modify the
        // collection Entries() enumerates.
        var entries = _db.ChangeTracker.Entries<TenantEntity>()
            .Where(e => e.Entity.Id == tenantId)
            .ToList();
        foreach (var entry in entries)
        {
            var property = entry.Property(t => t.CurrentStorageMB);
            property.OriginalValue = currentMB;
            property.CurrentValue = currentMB;
            property.IsModified = false;
        }
    }

    /// <summary>
    /// Calculates total size and file count of a directory in a single pass.
    /// Uses EnumerateFiles for streaming instead of GetFiles to reduce memory pressure.
    /// </summary>
    private static (int fileCount, long totalBytes) CalculateDirectorySizeAndCount(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            return (0, 0);
        }

        try
        {
            int count = 0;
            long total = 0;

            // Use EnumerateFiles for streaming - more memory efficient for large directories
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    total += fileInfo.Length;
                    count++;
                }
                catch (IOException)
                {
                    // File may have been deleted during enumeration, skip it
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip files we can't access
                }
            }

            return (count, total);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Writes .storage.json metadata file
    /// </summary>
    private async Task WriteStorageMetadataAsync(
        string tenantDir,
        string tenantId,
        long totalSizeBytes,
        double totalSizeMB,
        int fileCount)
    {
        var metadata = new
        {
            tenantId = tenantId,
            calculatedAt = DateTime.UtcNow.ToString("O"),
            totalSizeBytes = totalSizeBytes,
            totalSizeMB = Math.Round(totalSizeMB, 2),
            fileCount = fileCount
        };

        try
        {
            Directory.CreateDirectory(tenantDir);
            var metadataPath = Path.Combine(tenantDir, ".storage.json");
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(metadataPath, json);

            _logger.LogDebug(
                "WriteStorageMetadata: Written metadata for tenant {TenantId} to {Path}",
                tenantId, metadataPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WriteStorageMetadata: Failed to write metadata for tenant {TenantId}", tenantId);
        }
    }
}
