using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Wipes a tenant's content but not the tenant. See <see cref="ITenantDataPurgeService"/>
/// for the exact kept/removed split.
///
/// Every query is written with IgnoreQueryFilters() plus an explicit TenantId predicate:
/// this runs in a superadmin request whose ambient tenant context is a *different* tenant
/// (or none), so the global filters cannot be relied on here.
/// </summary>
public class TenantDataPurgeService : ITenantDataPurgeService
{
    private readonly ApplicationDbContext _db;
    private readonly IStorageQuotaService _quotaService;
    private readonly ITenantFilePathService _filePathService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantDataPurgeService> _logger;

    public TenantDataPurgeService(
        ApplicationDbContext db,
        IStorageQuotaService quotaService,
        ITenantFilePathService filePathService,
        IConfiguration configuration,
        ILogger<TenantDataPurgeService> logger)
    {
        _db = db;
        _quotaService = quotaService;
        _filePathService = filePathService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PurgeTenantDataResultDto> PurgeAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new ArgumentException($"Tenant {tenantId} not found");

        var result = new PurgeTenantDataResultDto
        {
            TenantId = tenant.Id,
            TenantName = tenant.Name
        };

        var gridStorage = _configuration["GridStorage"] ?? "map";

        // Map ids are needed after the rows are gone (cache busting + SSE), so grab them first.
        result.DeletedMapIds = await _db.Maps
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Id)
            .ToListAsync(ct);

        // The cookbook (Foods/FoodVariants) is deliberately NOT purged: it holds
        // player-contributed data that no re-import can restore, and it costs no tile storage.
        await PurgeDatabaseRowsAsync(tenantId, result, ct);

        await PurgeFilesAsync(tenantId, gridStorage, result);

        // Rewrites CurrentStorageMB + .storage.json from what is actually left on disk.
        await _quotaService.RecalculateStorageUsageAsync(tenantId, gridStorage);

        // Uploads land straight after a purge; make sure the directory skeleton is back.
        _filePathService.EnsureTenantDirectoriesExist(tenantId, gridStorage);

        _logger.LogWarning(
            "Purged tenant {TenantId} content: {Maps} maps, {Grids} grids, {Tiles} tiles, " +
            "{Markers} markers, {CustomMarkers} custom markers, " +
            "{Files} files ({MB:F2} MB freed)",
            tenantId, result.Maps, result.Grids, result.Tiles, result.Markers,
            result.CustomMarkers, result.FilesDeleted, result.MegabytesFreed);

        return result;
    }

    /// <summary>
    /// Deletes in child-before-parent order so SQLite's foreign keys never block a statement,
    /// and so marker-attached timers are gone before the markers they would be nulled against.
    /// </summary>
    private async Task PurgeDatabaseRowsAsync(
        string tenantId,
        PurgeTenantDataResultDto result,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // TimerWarnings has no TenantId; it cascades from Timers.
        result.Timers = await _db.Timers.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.TimerHistory = await _db.TimerHistory.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Notifications = await _db.Notifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Pings = await _db.Pings.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Roads = await _db.Roads.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Overlays = await _db.OverlayData.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Overlays += await _db.OverlayOffsets.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.DirtyZoomTiles = await _db.DirtyZoomTiles.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.CustomMarkers = await _db.CustomMarkers.IgnoreQueryFilters()
            .Where(cm => cm.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Markers = await _db.Markers.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Tiles = await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Grids = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId).ExecuteDeleteAsync(ct);

        result.Maps = await _db.Maps.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId).ExecuteDeleteAsync(ct);

        // Public maps built from this tenant would otherwise keep pointing at deleted map ids.
        result.PublicMapSources = await _db.PublicMapSources
            .Where(s => s.TenantId == tenantId).ExecuteDeleteAsync(ct);

        await _db.PublicMapSourceAlignments
            .Where(a => a.SourceTenantId == tenantId).ExecuteDeleteAsync(ct);

        // Keep the tenant's config, minus the pointer to a map that no longer exists.
        await _db.Config.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Key == "mainMapId").ExecuteDeleteAsync(ct);

        await transaction.CommitAsync(ct);

        // ExecuteDelete bypasses the change tracker, which may still hold now-deleted entities.
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Drops the tenant's whole storage tree (grid pngs, zoom tiles, large webp tiles) plus its
    /// map previews, measuring first so the caller can report what was reclaimed.
    /// </summary>
    private async Task PurgeFilesAsync(string tenantId, string gridStorage, PurgeTenantDataResultDto result)
    {
        var tenantsRoot = Path.GetFullPath(Path.Combine(gridStorage, "tenants"));
        var tenantDir = Path.GetFullPath(_filePathService.GetTenantDirectory(tenantId, gridStorage));

        // Guard against a tenant id that escapes its own directory before anything recursive runs.
        if (!tenantDir.StartsWith(tenantsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Refusing to purge '{tenantDir}': outside the tenant storage root '{tenantsRoot}'");
        }

        await DeleteTreeAsync(tenantDir, result);
        await DeleteTreeAsync(Path.GetFullPath(Path.Combine(gridStorage, "previews", tenantId)), result);
    }

    private async Task DeleteTreeAsync(string directory, PurgeTenantDataResultDto result)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var (fileCount, bytes) = MeasureDirectory(directory);

        // Windows releases handles lazily, so a recursive delete can fail with "directory is
        // not empty" on the first try even when nothing is really holding the tree open.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                result.FilesDeleted += fileCount;
                result.BytesFreed += bytes;
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200 * attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete directory {Directory}", directory);
                result.Warnings.Add($"Could not fully delete '{directory}': {ex.Message}");

                // Partial deletes are possible; count only what actually went away.
                var (remainingFiles, remainingBytes) = MeasureDirectory(directory);
                result.FilesDeleted += Math.Max(0, fileCount - remainingFiles);
                result.BytesFreed += Math.Max(0, bytes - remainingBytes);
                return;
            }
        }
    }

    private static (int fileCount, long totalBytes) MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var count = 0;
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                    count++;
                }
                catch (IOException)
                {
                    // Vanished mid-enumeration.
                }
                catch (UnauthorizedAccessException)
                {
                    // Not readable; ignore for the size estimate.
                }
            }
        }
        catch (Exception)
        {
            // Directory disappeared or is unreadable — report what was counted so far.
        }

        return (count, total);
    }
}
