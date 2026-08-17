using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Wipes a rectangular region of one map. See <see cref="IMapRegionWipeService"/> for the exact
/// removed/kept split.
///
/// Every query is written with IgnoreQueryFilters() plus an explicit TenantId predicate: this
/// runs in a superadmin request whose ambient tenant context is a *different* tenant (or none),
/// so the global filters cannot be relied on here (same rationale as TenantDataPurgeService).
/// </summary>
public class MapRegionWipeService : IMapRegionWipeService
{
    // SQLite parameter-count safety for Contains() chunks (same as GetExistingGridIdsAsync).
    private const int ChunkSize = 500;

    private readonly ApplicationDbContext _db;
    private readonly IStorageQuotaService _quotaService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MapRegionWipeService> _logger;

    public MapRegionWipeService(
        ApplicationDbContext db,
        IStorageQuotaService quotaService,
        IConfiguration configuration,
        ILogger<MapRegionWipeService> logger)
    {
        _db = db;
        _quotaService = quotaService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<MapRegionWipePreviewDto> PreviewAsync(
        string tenantId, int mapId, int x1, int y1, int x2, int y2, CancellationToken ct = default)
    {
        (x1, x2) = (Math.Min(x1, x2), Math.Max(x1, x2));
        (y1, y2) = (Math.Min(y1, y2), Math.Max(y1, y2));
        await ValidateTenantAndMapAsync(tenantId, mapId, ct);

        var preview = new MapRegionWipePreviewDto
        {
            TenantId = tenantId,
            MapId = mapId,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2
        };

        preview.Grids = await InBoxGrids(tenantId, mapId, x1, y1, x2, y2).CountAsync(ct);

        var inBoxGridIds = await InBoxGrids(tenantId, mapId, x1, y1, x2, y2)
            .Select(g => g.Id)
            .ToListAsync(ct);
        preview.Markers = await CountMarkersOnGridsAsync(tenantId, inBoxGridIds, ct);

        preview.Zoom0Tiles = await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.MapId == mapId && t.Zoom == 0
                        && t.CoordX >= x1 && t.CoordX <= x2 && t.CoordY >= y1 && t.CoordY <= y2)
            .CountAsync(ct);

        preview.Overlays = await _db.OverlayData.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.MapId == mapId
                        && o.CoordX >= x1 && o.CoordX <= x2 && o.CoordY >= y1 && o.CoordY <= y2)
            .CountAsync(ct);

        var allMapGrids = await _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.Map == mapId)
            .Select(g => new { g.CoordX, g.CoordY })
            .ToListAsync(ct);

        preview.MapTotalGrids = allMapGrids.Count;
        preview.PercentOfMap = allMapGrids.Count > 0
            ? Math.Round(100.0 * preview.Grids / allMapGrids.Count, 2)
            : 0;
        if (allMapGrids.Count > 0)
        {
            preview.MapExtentMinX = allMapGrids.Min(g => g.CoordX);
            preview.MapExtentMaxX = allMapGrids.Max(g => g.CoordX);
            preview.MapExtentMinY = allMapGrids.Min(g => g.CoordY);
            preview.MapExtentMaxY = allMapGrids.Max(g => g.CoordY);
        }

        return preview;
    }

    public async Task<MapRegionWipeResultDto> WipeAsync(
        string tenantId, int mapId, int x1, int y1, int x2, int y2, CancellationToken ct = default)
    {
        (x1, x2) = (Math.Min(x1, x2), Math.Max(x1, x2));
        (y1, y2) = (Math.Min(y1, y2), Math.Max(y1, y2));
        await ValidateTenantAndMapAsync(tenantId, mapId, ct);

        var gridStorage = _configuration["GridStorage"] ?? "map";
        var result = new MapRegionWipeResultDto
        {
            TenantId = tenantId,
            MapId = mapId,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2
        };

        // Captured up front — the rows are gone by the time files are deleted / counts reported.
        var inBoxGridIds = await InBoxGrids(tenantId, mapId, x1, y1, x2, y2)
            .Select(g => g.Id)
            .ToListAsync(ct);

        var inBoxTileFiles = await _db.Tiles.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.MapId == mapId && t.Zoom == 0
                        && t.CoordX >= x1 && t.CoordX <= x2 && t.CoordY >= y1 && t.CoordY <= y2)
            .Select(t => t.File)
            .ToListAsync(ct);

        var inBoxMarkerIds = new List<int>();
        foreach (var chunk in Chunk(inBoxGridIds))
        {
            inBoxMarkerIds.AddRange(await _db.Markers.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && chunk.Contains(m.GridId))
                .Select(m => m.Id)
                .ToListAsync(ct));
        }

        // Child-before-parent, one transaction: marker-attached timers (TimerWarnings cascade
        // from Timers) -> markers -> overlays -> zoom-0 tiles -> grids. Zoom 1-6 tile rows are
        // deliberately untouched.
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            foreach (var chunk in Chunk(inBoxMarkerIds))
            {
                result.Timers += await _db.Timers.IgnoreQueryFilters()
                    .Where(t => t.TenantId == tenantId && t.MarkerId != null && chunk.Contains(t.MarkerId.Value))
                    .ExecuteDeleteAsync(ct);
            }

            foreach (var chunk in Chunk(inBoxMarkerIds))
            {
                result.Markers += await _db.Markers.IgnoreQueryFilters()
                    .Where(m => m.TenantId == tenantId && chunk.Contains(m.Id))
                    .ExecuteDeleteAsync(ct);
            }

            result.Overlays = await _db.OverlayData.IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && o.MapId == mapId
                            && o.CoordX >= x1 && o.CoordX <= x2 && o.CoordY >= y1 && o.CoordY <= y2)
                .ExecuteDeleteAsync(ct);

            result.Zoom0Tiles = await _db.Tiles.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && t.MapId == mapId && t.Zoom == 0
                            && t.CoordX >= x1 && t.CoordX <= x2 && t.CoordY >= y1 && t.CoordY <= y2)
                .ExecuteDeleteAsync(ct);

            result.Grids = await _db.Grids.IgnoreQueryFilters()
                .Where(g => g.TenantId == tenantId && g.Map == mapId
                            && g.CoordX >= x1 && g.CoordX <= x2 && g.CoordY >= y1 && g.CoordY <= y2)
                .ExecuteDeleteAsync(ct);

            await transaction.CommitAsync(ct);
        }

        // ExecuteDelete bypasses the change tracker, which may still hold now-deleted entities.
        _db.ChangeTracker.Clear();

        DeleteTileFiles(tenantId, gridStorage, inBoxTileFiles, result);

        // Rewrites CurrentStorageMB from what is actually left on disk.
        await _quotaService.RecalculateStorageUsageAsync(tenantId, gridStorage);

        _logger.LogWarning(
            "Wiped region [{X1},{Y1}]..[{X2},{Y2}] of map {MapId} (tenant {TenantId}): " +
            "{Grids} grids, {Markers} markers, {Timers} timers, {Tiles} zoom-0 tiles, {Overlays} overlays, " +
            "{Files} files ({MB:F2} MB freed). Zoom 1-6 tiles retained.",
            x1, y1, x2, y2, mapId, tenantId,
            result.Grids, result.Markers, result.Timers, result.Zoom0Tiles, result.Overlays,
            result.FilesDeleted, result.MegabytesFreed);

        return result;
    }

    private async Task ValidateTenantAndMapAsync(string tenantId, int mapId, CancellationToken ct)
    {
        var tenantExists = await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists)
        {
            throw new ArgumentException($"Tenant {tenantId} not found");
        }

        var mapExists = await _db.Maps.IgnoreQueryFilters()
            .AnyAsync(m => m.Id == mapId && m.TenantId == tenantId, ct);
        if (!mapExists)
        {
            throw new ArgumentException($"Map {mapId} not found in tenant {tenantId}");
        }
    }

    private IQueryable<GridDataEntity> InBoxGrids(string tenantId, int mapId, int x1, int y1, int x2, int y2)
        => _db.Grids.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.Map == mapId
                        && g.CoordX >= x1 && g.CoordX <= x2 && g.CoordY >= y1 && g.CoordY <= y2);

    private async Task<int> CountMarkersOnGridsAsync(string tenantId, List<string> gridIds, CancellationToken ct)
    {
        var count = 0;
        foreach (var chunk in Chunk(gridIds))
        {
            count += await _db.Markers.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && chunk.Contains(m.GridId))
                .CountAsync(ct);
        }
        return count;
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> items)
    {
        for (var i = 0; i < items.Count; i += ChunkSize)
        {
            yield return items.GetRange(i, Math.Min(ChunkSize, items.Count - i));
        }
    }

    /// <summary>
    /// Best-effort deletion of the captured zoom-0 tile files. Each Tiles.File value is a
    /// relative path; a poisoned value must never escape the tenant storage root, so every
    /// resolved path is containment-checked before deletion (same guard as the tenant purge).
    /// </summary>
    private void DeleteTileFiles(string tenantId, string gridStorage, List<string> tileFiles, MapRegionWipeResultDto result)
    {
        var tenantsRoot = Path.GetFullPath(Path.Combine(gridStorage, "tenants"));

        foreach (var file in tileFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(gridStorage, file));
            }
            catch (Exception)
            {
                result.Warnings.Add($"Skipped tile file with unresolvable path '{file}'");
                continue;
            }

            if (!fullPath.StartsWith(tenantsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"Skipped tile file outside the tenant storage root: '{file}'");
                continue;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    result.Warnings.Add($"Tile file already missing: '{file}'");
                    continue;
                }

                var size = new FileInfo(fullPath).Length;
                File.Delete(fullPath);
                result.FilesDeleted++;
                result.BytesFreed += size;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete tile file {File} for tenant {TenantId}", file, tenantId);
                result.Warnings.Add($"Could not delete '{file}': {ex.Message}");
            }
        }
    }
}
